using Dapper;
using MarkCheck.Data;
using MarkCheck.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace MarkCheck.Services;

/// <summary>Основная бизнес-логика обработки отсканированного кода.</summary>
public class ScanProcessor
{
    private readonly Db _db;
    private readonly TrueApiClient _cz;
    private readonly IHubContext<ScanHub> _hub;
    private readonly ILogger<ScanProcessor> _log;
    private static readonly SemaphoreSlim _gate = new(1, 1); // сканирования обрабатываем строго по очереди

    public ScanProcessor(Db db, TrueApiClient cz, IHubContext<ScanHub> hub, ILogger<ScanProcessor> log)
    { _db = db; _cz = cz; _hub = hub; _log = log; }

    /// <summary>Текущий активный ордер (черновик проверки).</summary>
    public Order GetActiveOrder()
    {
        using var c = _db.Open();
        return c.QueryFirstOrDefault<Order>(
            "SELECT * FROM Orders WHERE Status='active' ORDER BY Id DESC LIMIT 1");
    }

    public async Task<ScanResult> ProcessAsync(string raw, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await ProcessInnerAsync(raw, ct); }
        finally { _gate.Release(); }
    }

    private async Task<ScanResult> ProcessInnerAsync(string raw, CancellationToken ct)
    {
        var now = DateTime.Now.ToString("s");
        var order = GetActiveOrder();
        if (order == null)
            return Fail(raw, "Нет открытого ордера. Загрузите расходный ордер для проверки.");

        // ---- Разбор кода ----
        var code = Gs1Parser.Parse(raw);
        string gtin = code.Gtin, serial = code.Serial, cis = code.Cis;
        bool isMarkedCode = code.IsDataMatrix && cis != null;

        if (!isMarkedCode)
        {
            var digits = raw.Trim();
            if (digits.Length is >= 8 and <= 14 && digits.All(char.IsDigit))
            {
                gtin = digits.PadLeft(14, '0');       // обычный EAN-13/8 для немаркируемого товара
                cis = "EAN:" + gtin + ":" + Guid.NewGuid().ToString("N").Substring(0, 8);
            }
            else
            {
                return Fail(raw, code.Error ?? "Код не распознан", needSetAside: true, order.Id);
            }
        }

        using var c = _db.Open();

        // ---- Дубликаты ----
        if (isMarkedCode)
        {
            var dupSame = c.QueryFirstOrDefault<ScannedItem>(
                "SELECT * FROM ScannedItems WHERE OrderId=@o AND Cis=@c", new { o = order.Id, c = cis });
            if (dupSame != null)
                return Fail(raw, "Этот код уже отсканирован в текущем ордере", needSetAside: true, order.Id, "duplicate");

            var dupOther = c.QueryFirstOrDefault<string>(
                "SELECT o.OrderNumber FROM ScannedItems s JOIN Orders o ON o.Id=s.OrderId " +
                "WHERE s.Cis=@c AND s.OrderId<>@o AND s.SetAside=0 LIMIT 1", new { c = cis, o = order.Id });
            if (dupOther != null)
                return Fail(raw, $"Код уже был отгружен в ордере {dupOther}", needSetAside: true, order.Id, "duplicate");
        }

        // ---- GTIN → позиции базы ШК → строка ордера ----
        // Один GTIN может принадлежать нескольким номенклатурам (дубли в 1С) — выбираем ту, что есть в ордере.
        var skus = FindSkus(c, gtin);
        if (skus.Count == 0)
        {
            return Fail(raw, $"Штрихкода {gtin} нет в базе ШК — отложите", needSetAside: true, order.Id);
        }
        var keys = skus.Select(x => x.SkuKey).Distinct().ToList();
        var lines = c.Query<OrderLine>("SELECT * FROM OrderLines WHERE OrderId=@o ORDER BY LineNo", new { o = order.Id })
                     .Where(l => l.SkuKey != null && keys.Contains(l.SkuKey)).ToList();
        var sku = lines.Count > 0 ? skus.First(x => x.SkuKey == lines[0].SkuKey) : skus[0];
        var pName = string.IsNullOrWhiteSpace(sku.Characteristic) ? sku.Name : $"{sku.Name} ({sku.Characteristic.Trim()})";
        if (lines.Count == 0)
        {
            SaveScan(c, order.Id, null, pName, sku.Code1C, raw, cis, gtin, serial, "error", $"Товар «{pName}» отсутствует в ордере", now, setAside: true);
            return Fail(raw, $"Товара «{pName}» нет в ордере — отложите", needSetAside: true, order.Id);
        }
        // Одна позиция может быть разбита на несколько строк (разные серии) — заполняем по очереди.
        OrderLine line = null;
        foreach (var l in lines)
        {
            int cnt = c.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM ScannedItems WHERE OrderId=@o AND OrderLineId=@l AND SetAside=0 AND ValidationStatus IN ('ok','pending')",
                new { o = order.Id, l = l.Id });
            if (cnt < l.Qty) { line = l; break; }
        }
        line ??= lines[^1];
        int scanned = c.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM ScannedItems WHERE OrderId=@o AND OrderLineId=@l AND SetAside=0 AND ValidationStatus IN ('ok','pending')",
            new { o = order.Id, l = line.Id });
        if (scanned >= line.Qty)
        {
            SaveScan(c, order.Id, line, pName, sku.Code1C, raw, cis, gtin, serial, "error",
                     $"Превышение количества: по ордеру {line.Qty:0.###}", now, setAside: true);
            return Fail(raw, $"Превышение количества по «{pName}» (в ордере {line.Qty:0.###}) — отложите",
                        needSetAside: true, order.Id);
        }

        // ---- Предварительная запись (pending) ----
        long id = SaveScan(c, order.Id, line, pName, sku.Code1C, raw, cis, gtin, serial, "pending", "Проверка в ЧЗ…", now);
        await _hub.Clients.All.SendAsync("scanUpdate", new ScanResult
        {
            Status = "pending", ScanId = id, OrderId = order.Id, OrderLineId = line.Id,
            Gtin = gtin, Serial = serial, DataMatrix = raw,
            ProductName = pName, Code1C = sku.Code1C, Message = "Проверка в ЧЗ…"
        }, ct);

        // ---- Проверка в Честном ЗНАКе ----
        CzCheckResult cz;
        if (isMarkedCode && line.RequiresMarking)
            cz = await _cz.CheckAsync(cis, ct);
        else
            cz = new CzCheckResult { Ok = true, Skipped = true, Message = "Товар без маркировки — проверка ЧЗ не требуется" };

        var status = cz.Ok ? "ok" : "error";
        c.Execute("UPDATE ScannedItems SET ValidationStatus=@s, ValidationMessage=@m, RawResponse=@r, SetAside=@sa WHERE Id=@id",
            new { s = status, m = cz.Message, r = cz.Raw, sa = cz.Ok ? 0 : 1, id });

        var result = new ScanResult
        {
            Status = status,
            Message = cz.Message,
            ScanId = id,
            OrderId = order.Id,
            OrderLineId = line.Id,
            Gtin = gtin,
            Serial = serial,
            DataMatrix = raw,
            ProductName = pName,
            Code1C = sku.Code1C,
            NeedSetAside = !cz.Ok
        };
        await _hub.Clients.All.SendAsync("scanUpdate", result, ct);
        return result;
    }

    private long SaveScan(Microsoft.Data.Sqlite.SqliteConnection c, long orderId, OrderLine line, string name, string code1C,
        string raw, string cis, string gtin, string serial, string status, string msg, string now, bool setAside = false)
    {
        return c.ExecuteScalar<long>(@"
INSERT INTO ScannedItems(OrderId,OrderLineId,ProductId,DataMatrix,Cis,Gtin,Serial,ValidationStatus,ValidationMessage,ProductName,Code1C,IsMapped,SetAside,CreatedAt)
VALUES(@OrderId,@OrderLineId,@ProductId,@DataMatrix,@Cis,@Gtin,@Serial,@Status,@Msg,@Name,@Code1C,1,@SetAside,@Now);
SELECT last_insert_rowid();",
            new
            {
                OrderId = orderId,
                OrderLineId = line?.Id,
                ProductId = (long?)null,
                DataMatrix = raw,
                Cis = cis,
                Gtin = gtin,
                Serial = serial,
                Status = status,
                Msg = msg,
                Name = name,
                Code1C = code1C,
                SetAside = setAside ? 1 : 0,
                Now = now
            });
    }

    private ScanResult Fail(string raw, string msg, bool needSetAside = false, long? orderId = null, string status = "error")
        => new ScanResult { Status = status, Message = msg, DataMatrix = raw, NeedSetAside = needSetAside, OrderId = orderId };

    /// <summary>Позиции базы ШК по GTIN (с учётом ведущих нулей: в базе GTIN хранится 14-значным).</summary>
    private static List<BarcodeRow> FindSkus(Microsoft.Data.Sqlite.SqliteConnection c, string gtin)
    {
        var g = (gtin ?? "").PadLeft(14, '0');
        return c.Query<BarcodeRow>("SELECT * FROM Barcodes WHERE Gtin=@g", new { g }).ToList();
    }
}
