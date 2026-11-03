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

        // ---- Сопоставление GTIN с номенклатурой ----
        var product = FindProductByGtin(c, gtin);
        if (product == null)
        {
            return new ScanResult
            {
                Status = "need_mapping",
                Message = $"GTIN {gtin} не сопоставлен. Укажите товар из ордера.",
                OrderId = order.Id,
                Gtin = gtin,
                Serial = serial,
                DataMatrix = raw
            };
        }

        // ---- Поиск строки ордера ----
        // Code1C из логики сопоставления исключён полностью (в 1С он мог отличаться от справочника и был источником пересортицы).
        // Приоритет: (1) по GTIN — точный идентификатор конкретной характеристики (цвет/размер);
        // (2) по наименованию с нормализацией текста (регистр, пробелы, разные варианты слеша \ и /),
        // если GTIN у строки ордера пока не проставлен.
        var line = c.QueryFirstOrDefault<OrderLine>(
            "SELECT * FROM OrderLines WHERE OrderId=@o AND Gtin=@gtin", new { o = order.Id, gtin = product.Gtin });
        if (line == null)
        {
            var byName = FindLinesByNormalizedName(c, order.Id, product.Name);
            if (byName.Count == 1)
            {
                line = byName[0];
            }
            else if (byName.Count > 1)
            {
                // Несколько строк с одинаковым наименованием и без GTIN — однозначно выбрать невозможно,
                // просим оператора сопоставить вручную вместо тихой записи в первую подвернувшуюся.
                return new ScanResult
                {
                    Status = "need_mapping",
                    Message = $"В ордере несколько строк с таким наименованием — выберите нужную вручную",
                    OrderId = order.Id,
                    Gtin = product.Gtin,
                    Serial = serial,
                    DataMatrix = raw
                };
            }
        }
        if (line == null)
        {
            SaveScan(c, order.Id, null, product, raw, cis, gtin, serial, "error",
                     $"Товар «{product.Name}» отсутствует в ордере", now, setAside: true);
            return Fail(raw, $"Товара «{product.Name}» нет в ордере — отложите", needSetAside: true, order.Id);
        }

        int scanned = c.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM ScannedItems WHERE OrderId=@o AND OrderLineId=@l AND SetAside=0 AND ValidationStatus IN ('ok','pending')",
            new { o = order.Id, l = line.Id });
        if (scanned >= line.Qty)
        {
            SaveScan(c, order.Id, line, product, raw, cis, gtin, serial, "error",
                     $"Превышение количества: по ордеру {line.Qty:0.###}", now, setAside: true);
            return Fail(raw, $"Превышение количества по «{product.Name}» (в ордере {line.Qty:0.###}) — отложите",
                        needSetAside: true, order.Id);
        }

        // ---- Предварительная запись (pending) ----
        long id = SaveScan(c, order.Id, line, product, raw, cis, gtin, serial, "pending", "Проверка в ЧЗ…", now);
        await _hub.Clients.All.SendAsync("scanUpdate", new ScanResult
        {
            Status = "pending", ScanId = id, OrderId = order.Id, OrderLineId = line.Id,
            Gtin = gtin, Serial = serial, DataMatrix = raw,
            ProductName = product.Name, Code1C = product.Code1C, Message = "Проверка в ЧЗ…"
        }, ct);

        // ---- Проверка в Честном ЗНАКе ----
        CzCheckResult cz;
        if (isMarkedCode && product.Marked && line.RequiresMarking)
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
            ProductName = product.Name,
            Code1C = product.Code1C,
            NeedSetAside = !cz.Ok
        };
        await _hub.Clients.All.SendAsync("scanUpdate", result, ct);
        return result;
    }

    /// <summary>
    /// Сопоставление нового GTIN с конкретной строкой ордера, выбранной оператором в модалке (orderLineId),
    /// и повторная обработка кода. Выбор по Id однозначен и не зависит от текста или кода 1С.
    /// </summary>
    public async Task<ScanResult> MapAndRescanAsync(string gtin, long orderLineId, string rawCode, CancellationToken ct = default)
    {
        using (var c = _db.Open())
        {
            var order = GetActiveOrder();
            var line = c.QueryFirstOrDefault<OrderLine>(
                "SELECT * FROM OrderLines WHERE Id=@id AND OrderId=@o", new { id = orderLineId, o = order?.Id });
            if (line == null) return Fail(rawCode, "Строка ордера не найдена");
            // GTIN — физический идентификатор конкретной характеристики товара — привязываем его именно к выбранной строке:
            // в справочнике Products (Code1C и Name из line, а не из входных параметров) и в саму строку OrderLines.Gtin —
            // чтобы следующие сканы этого же GTIN попадали туда же, независимо от текста наименования.
            c.Execute("INSERT INTO Products(Code1C,Name,Gtin,Marked) VALUES(@c,@n,@g,@m) " +
                      "ON CONFLICT(Gtin) DO UPDATE SET Code1C=excluded.Code1C, Name=excluded.Name",
                      new { c = line.Code1C, n = line.Name, g = gtin, m = line.RequiresMarking ? 1 : 0 });
            c.Execute("UPDATE OrderLines SET Gtin=@g WHERE Id=@id", new { g = gtin, id = line.Id });
        }
        return await ProcessAsync(rawCode, ct);
    }

    /// <summary>Ищет в ордере строки, у которых наименование совпадает с заданным после нормализации
    /// (регистр, лишние пробелы, разные варианты слеша \ и /). Сравнение выполняется в C#,
    /// так как SQLite по умолчанию не знает о регистронезависимых сравнениях кириллицы.</summary>
    private static List<OrderLine> FindLinesByNormalizedName(Microsoft.Data.Sqlite.SqliteConnection c, long orderId, string name)
    {
        var target = NormalizeName(name);
        if (target.Length == 0) return new List<OrderLine>();
        return c.Query<OrderLine>("SELECT * FROM OrderLines WHERE OrderId=@o", new { o = orderId })
            .Where(l => NormalizeName(l.Name) == target)
            .ToList();
    }

    /// <summary>Приводит наименование к единому виду для сравнения: нижний регистр, без лишних пробелов,
    /// слеши \ и / сведены к одному виду, игнорируются пробелы вокруг скобок/тире.</summary>
    private static string NormalizeName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var t = s.Trim().ToLowerInvariant();
        t = t.Replace('\\', '/');
        // сворачиваем повторяющиеся пробелы и пробелы вокруг разделителей
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*/\s*", "/");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");
        return t.Trim();
    }

    private long SaveScan(Microsoft.Data.Sqlite.SqliteConnection c, long orderId, OrderLine line, Product p,
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
                ProductId = p?.Id,
                DataMatrix = raw,
                Cis = cis,
                Gtin = gtin,
                Serial = serial,
                Status = status,
                Msg = msg,
                Name = p?.Name,
                Code1C = p?.Code1C,
                SetAside = setAside ? 1 : 0,
                Now = now
            });
    }

    private ScanResult Fail(string raw, string msg, bool needSetAside = false, long? orderId = null, string status = "error")
        => new ScanResult { Status = status, Message = msg, DataMatrix = raw, NeedSetAside = needSetAside, OrderId = orderId };

    /// <summary>
    /// Ищет товар по GTIN с учётом возможного расхождения в ведущих нулях:
    /// в справочнике код может быть сохранён без ведущего ноля (13 цифр), а сканер читает
    /// полный 14-значный GTIN из Data Matrix (и наоборот).
    /// Порядок попыток: (1) точное совпадение, (2) без ведущего ноля, (3) дополненный нулями до 14 знаков.
    /// </summary>
    private static Product FindProductByGtin(Microsoft.Data.Sqlite.SqliteConnection c, string gtin)
    {
        var product = c.QueryFirstOrDefault<Product>("SELECT * FROM Products WHERE Gtin=@g", new { g = gtin });
        if (product != null) return product;

        var stripped = gtin.TrimStart('0');
        if (stripped.Length > 0 && stripped != gtin)
        {
            product = c.QueryFirstOrDefault<Product>("SELECT * FROM Products WHERE Gtin=@g", new { g = stripped });
            if (product != null) return product;
        }

        var padded = gtin.PadLeft(14, '0');
        if (padded != gtin)
            product = c.QueryFirstOrDefault<Product>("SELECT * FROM Products WHERE Gtin=@g", new { g = padded });

        return product;
    }
}
