using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using MarkCheck.Data;
using MarkCheck.Hubs;
using MarkCheck.Services;
using Microsoft.Extensions.FileProviders;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // при сборке в один файл статика лежит рядом с exe
    ContentRootPath = AppContext.BaseDirectory
});

// Каталог данных — всегда рядом с exe (при сборке в один файл BaseDirectory указывает на распакованный временный каталог)
var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
if (string.IsNullOrEmpty(exeDir)) exeDir = AppContext.BaseDirectory;
var dataDir = Path.Combine(exeDir, "data");
Directory.CreateDirectory(dataDir);
var db = new Db(Path.Combine(dataDir, "markcheck.db"));
db.Init();

builder.Services.AddSingleton(db);
builder.Services.AddHttpClient();
builder.Services.AddSignalR();
builder.Services.AddScoped<TrueApiClient>();
builder.Services.AddScoped<ScanProcessor>();
builder.Services.AddSingleton(new SecretProtector(dataDir));
builder.Services.AddSingleton<BitrixNotifier>();
builder.Services.AddSingleton<OrderFolderService>();
builder.Services.AddSingleton<BarcodeSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BarcodeSyncService>());
builder.Services.AddSingleton<ScannerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScannerService>());
builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");

var url = db.GetSetting("http_url", "http://0.0.0.0:5080");
builder.WebHost.UseUrls(url);

var app = builder.Build();

// Веб-интерфейс берётся из ресурсов, встроенных в exe.
// Если рядом с программой есть каталог wwwroot — он имеет приоритет (удобно для правки вёрстки без пересборки).
var providers = new List<IFileProvider>();
var localWww = Path.Combine(exeDir, "wwwroot");
if (Directory.Exists(localWww)) providers.Add(new PhysicalFileProvider(localWww));
providers.Add(new ManifestEmbeddedFileProvider(System.Reflection.Assembly.GetExecutingAssembly(), "wwwroot"));
var webFiles = new CompositeFileProvider(providers);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });
app.UseStaticFiles(new StaticFileOptions { FileProvider = webFiles });
app.MapHub<ScanHub>("/hub");

var ru = CultureInfo.GetCultureInfo("ru-RU");
string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

// ---------------------------------------------------------------- Состояние
app.MapGet("/api/state", (Db d, ScannerService sc) =>
{
    using var c = d.Open();
    var order = c.QueryFirstOrDefault<Order>("SELECT * FROM Orders WHERE Status='active' ORDER BY Id DESC LIMIT 1");
    if (order == null) return Results.Json(new { order = (object)null, scanner = sc.State });
    var lines = c.Query<OrderLine>("SELECT * FROM OrderLines WHERE OrderId=@o ORDER BY LineNo", new { o = order.Id }).ToList();
    var counts = c.Query<LineCount>("SELECT OrderLineId, COUNT(*) Cnt FROM ScannedItems WHERE OrderId=@o AND SetAside=0 AND ValidationStatus IN ('ok','pending') GROUP BY OrderLineId", new { o = order.Id })
                  .Where(x => x.OrderLineId.HasValue).ToDictionary(x => x.OrderLineId.Value, x => x.Cnt);
    foreach (var l in lines) l.ScannedQty = counts.TryGetValue(l.Id, out var n) ? n : 0;
    var scans = c.Query<ScannedItem>("SELECT * FROM ScannedItems WHERE OrderId=@o ORDER BY Id DESC", new { o = order.Id }).ToList();
    return Results.Json(new { order, lines, scans, scanner = sc.State });
});

app.MapGet("/api/scanner", (ScannerService sc) => Results.Json(new { state = sc.State, ports = ScannerService.AvailablePorts() }));

// ---------------------------------------------------------------- Загрузка ордера
app.MapPost("/api/orders/upload", async (HttpRequest req, Db d) =>
{
    if (!req.HasFormContentType) return Results.BadRequest(new { error = "Ожидается файл" });
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file == null) return Results.BadRequest(new { error = "Файл не выбран" });
    bool force = form["force"] == "1";

    using var c = d.Open();
    var active = c.QueryFirstOrDefault<Order>("SELECT * FROM Orders WHERE Status='active' ORDER BY Id DESC LIMIT 1");
    if (active != null && !force)
        return Results.Json(new { error = "active_exists", orderNumber = active.OrderNumber }, statusCode: 409);
    if (active != null) c.Execute("UPDATE Orders SET Status='closed', ClosedAt=@n WHERE Id=@id", new { n = Now(), id = active.Id });

    try
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;
        return Results.Json(CreateOrder(c, ms, file.FileName, null));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = "Ошибка разбора файла: " + ex.Message });
    }
});

// ---------------------------------------------------------------- Печатные формы в общей папке
// all: «1»/«true» — показывать и собранные. Строкой, т.к. bool в minimal API не принимает «0»/«1» (ответ 400).
app.MapGet("/api/files", (OrderFolderService f, string all = null) =>
{
    bool includeCollected = all == "1" || string.Equals(all, "true", StringComparison.OrdinalIgnoreCase);
    var files = f.List(includeCollected, out var error);
    return Results.Json(new { root = f.Root, error, files });
});

app.MapPost("/api/orders/open", (OpenRequest r, Db d, OrderFolderService f) =>
{
    using var c = d.Open();
    var active = c.QueryFirstOrDefault<Order>("SELECT * FROM Orders WHERE Status='active' ORDER BY Id DESC LIMIT 1");
    if (active != null && active.SourcePath == r.Path) return Results.Json(new { orderId = active.Id, orderNumber = active.OrderNumber });
    if (active != null && !r.Force)
        return Results.Json(new { error = "active_exists", orderNumber = active.OrderNumber }, statusCode: 409);
    if (active != null) c.Execute("UPDATE Orders SET Status='closed', ClosedAt=@n WHERE Id=@id", new { n = Now(), id = active.Id });
    try
    {
        using var ms = f.Read(r.Path);
        return Results.Json(CreateOrder(c, ms, Path.GetFileName(r.Path), r.Path));
    }
    catch (Exception ex) { return Results.BadRequest(new { error = "Не удалось открыть файл: " + ex.Message }); }
});

// Завершение сборки: отчёты, файл «КИЗ - …», префикс «СОБРАН - », отбивка в Битрикс24.
app.MapPost("/api/orders/{id:long}/finish", async (long id, FinishRequest r, Db d, OrderFolderService f, BitrixNotifier bx) =>
{
    if (r.Boxes < 1) return Results.BadRequest(new { error = "Укажите количество коробов" });
    var info = new List<string>();
    Order order;
    List<ScannedItem> ok;
    using (var c = d.Open())
    {
        order = c.QueryFirstOrDefault<Order>("SELECT * FROM Orders WHERE Id=@id", new { id });
        if (order == null) return Results.NotFound();
        CloseOrder(c, id);
        c.Execute("UPDATE Orders SET Boxes=@b WHERE Id=@id", new { b = r.Boxes, id });
        ok = c.Query<ScannedItem>("SELECT * FROM ScannedItems WHERE OrderId=@id AND ValidationStatus='ok' ORDER BY Id", new { id }).ToList();
    }
    int units = ok.Count;
    if (!string.IsNullOrEmpty(order.SourcePath))
    {
        var kizFormat = d.GetSetting("kiz_format", "cis");
        var codes = ok.Where(s => s.Cis != null && !s.Cis.StartsWith("EAN:"))
                      .Select(s => kizFormat == "full" ? s.DataMatrix : s.Cis).ToList();
        try { info.Add("Сохранён файл «" + f.WriteKiz(order.SourcePath, codes) + $"» ({codes.Count} кодов)"); }
        catch (Exception ex) { info.Add("Не удалось сохранить файл КИЗ: " + ex.Message); }
        try
        {
            var newPath = f.MarkCollected(order.SourcePath);
            info.Add("Печатная форма переименована: «" + Path.GetFileName(newPath) + "»");
            using var c = d.Open();
            c.Execute("UPDATE Orders SET SourcePath=@p WHERE Id=@id", new { p = newPath, id });
        }
        catch (Exception ex) { info.Add("Не удалось переименовать печатную форму (открыта у кого-то?): " + ex.Message); }

        var dialog = bx.ResolveDialog(order.SourcePath);
        if (dialog != null)
        {
            var err = await bx.SendAsync(dialog, BitrixNotifier.BuildFboMessage(order.Instruction ?? order.OrderNumber, units, r.Boxes));
            info.Add(err == null ? "Отбивка в Битрикс24 отправлена" : "Битрикс24: ошибка отправки — " + err);
        }
    }
    else info.Add("Ордер загружен вручную — файл КИЗ и переименование не выполняются");
    using (var c = d.Open())
        c.Execute("UPDATE Orders SET FinishInfo=@i WHERE Id=@id", new { i = string.Join("\n", info), id });
    return Results.Json(new { ok = true, units, boxes = r.Boxes, info });
});

app.MapPost("/api/orders/{id:long}/close", (long id, Db d) =>
{
    using var c = d.Open();
    CloseOrder(c, id);
    return Results.Ok(new { ok = true });
});

// Коды маркировки для окна копирования в браузере (сразу после завершения)
app.MapGet("/api/orders/{id:long}/codes", (long id, Db d) =>
{
    using var c = d.Open();
    var scans = c.Query<ScannedItem>("SELECT * FROM ScannedItems WHERE OrderId=@id AND ValidationStatus='ok' ORDER BY Id", new { id }).ToList();
    return Results.Json(new { codes = scans.Select(s => s.DataMatrix).ToList() });
});

// ---------------------------------------------------------------- Сканирование
app.MapPost("/api/scan", async (ScanRequest r, ScanProcessor p) =>
    Results.Json(await p.ProcessAsync(r.Code ?? "")));


app.MapPost("/api/scans/{id:long}/setaside", (long id, Db d) =>
{
    using var c = d.Open();
    c.Execute("UPDATE ScannedItems SET SetAside=1, ValidationStatus='setaside', ValidationMessage=COALESCE(ValidationMessage,'')||' | отложено оператором' WHERE Id=@id", new { id });
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/scans/{id:long}", (long id, Db d) =>
{
    using var c = d.Open();
    c.Execute("DELETE FROM ScannedItems WHERE Id=@id", new { id });
    return Results.Ok(new { ok = true });
});

// ---------------------------------------------------------------- База штрихкодов (отчёт по ШК из 1С)
app.MapGet("/api/barcodes", (Db d, BarcodeSyncService sync, string q = null) =>
{
    using var c = d.Open();
    var sql = "SELECT * FROM Barcodes" + (string.IsNullOrWhiteSpace(q) ? "" :
        " WHERE Gtin LIKE @p OR RawBarcode LIKE @p OR Code1C LIKE @p OR Name LIKE @p OR PrintName LIKE @p OR Article LIKE @p OR Characteristic LIKE @p")
        + " ORDER BY Name, Characteristic LIMIT 500";
    return Results.Json(new
    {
        total = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Barcodes"),
        last = d.GetSetting("barcodes_last", ""),
        rows = c.Query<BarcodeRow>(sql, new { p = "%" + q + "%" })
    });
});

app.MapPost("/api/barcodes/import", async (HttpRequest req, Db d) =>
{
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file == null) return Results.BadRequest(new { error = "Файл не выбран" });
    try
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;
        var rows = BarcodeImportParser.Parse(ms, out var warnings);
        if (rows.Count == 0) return Results.BadRequest(new { error = "В файле нет строк со штрихкодами" });
        BarcodeStore.Replace(d, rows);
        d.SetSetting("barcodes_last", $"{DateTime.Now:dd.MM HH:mm} загружено вручную {rows.Count} ШК из «{file.FileName}»");
        return Results.Json(new { imported = rows.Count, skipped = warnings.Count, warnings = warnings.Take(50) });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = "Не удалось разобрать файл: " + ex.Message }); }
});

app.MapPost("/api/barcodes/sync", (BarcodeSyncService sync) =>
{
    try { return Results.Json(new { message = sync.SyncNow(true) }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// ---------------------------------------------------------------- Журнал и статистика
app.MapGet("/api/journal", (Db d) =>
{
    using var c = d.Open();
    // Тип обязателен: динамические строки Dapper сериализуются в пустые {} (см. JournalRow).
    var rows = c.Query<JournalRow>(@"
SELECT o.Id, o.OrderNumber, o.FileName, o.Recipient, o.CreatedAt, o.ClosedAt, o.Status,
       (SELECT COUNT(*) FROM OrderLines l WHERE l.OrderId=o.Id) Lines,
       (SELECT COALESCE(SUM(Qty),0) FROM OrderLines l WHERE l.OrderId=o.Id) PlanQty,
       (SELECT COUNT(*) FROM ScannedItems s WHERE s.OrderId=o.Id AND s.ValidationStatus='ok') OkQty,
       (SELECT COUNT(*) FROM ScannedItems s WHERE s.OrderId=o.Id AND s.SetAside=1) BadQty
FROM Orders o ORDER BY o.Id DESC LIMIT 500");
    return Results.Json(rows);
});

app.MapGet("/api/journal/{id:long}", (long id, Db d) =>
{
    using var c = d.Open();
    var order = c.QueryFirstOrDefault<Order>("SELECT * FROM Orders WHERE Id=@id", new { id });
    var lines = c.Query<OrderLine>("SELECT * FROM OrderLines WHERE OrderId=@id ORDER BY LineNo", new { id }).ToList();
    var counts = c.Query<LineCount>("SELECT OrderLineId, COUNT(*) Cnt FROM ScannedItems WHERE OrderId=@id AND ValidationStatus='ok' GROUP BY OrderLineId", new { id })
                  .Where(x => x.OrderLineId.HasValue).ToDictionary(x => x.OrderLineId.Value, x => x.Cnt);
    foreach (var l in lines) l.ScannedQty = counts.TryGetValue(l.Id, out var v) ? v : 0;
    var scans = c.Query<ScannedItem>("SELECT * FROM ScannedItems WHERE OrderId=@id ORDER BY Id", new { id }).ToList();
    return Results.Json(new { order, lines, scans });
});

// ---------------------------------------------------------------- Выгрузка CSV (сохранённые отчёты)
// Простой CSV: только коды маркировки — для вставки в 1С оператором.
app.MapGet("/api/orders/{id:long}/csv", (long id, Db d) =>
{
    using var c = d.Open();
    var order = c.QueryFirstOrDefault<Order>("SELECT * FROM Orders WHERE Id=@id", new { id });
    if (order == null) return Results.NotFound();
    var report = c.QueryFirstOrDefault<OrderReport>("SELECT * FROM OrderReports WHERE OrderId=@id", new { id });
    // Отчёт может отсутствовать для ордеров, закрытых до появления сохранения в БД — строим на лету и сохраняем.
    var csv = report?.SimpleCsv;
    if (csv == null)
    {
        var scans = c.Query<ScannedItem>("SELECT * FROM ScannedItems WHERE OrderId=@id ORDER BY Id", new { id }).ToList();
        csv = BuildSimpleCsv(scans);
        c.Execute("INSERT INTO OrderReports(OrderId,SimpleCsv,CreatedAt) VALUES(@id,@s,@n) " +
                  "ON CONFLICT(OrderId) DO UPDATE SET SimpleCsv=excluded.SimpleCsv", new { id, s = csv, n = Now() });
    }
    var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
    var name = $"Коды_{(order.OrderNumber ?? order.Id.ToString()).Replace('/', '-')}.csv";
    return Results.File(bytes, "text/csv; charset=utf-8", name);
});

// Подробный CSV: сводка + все коды со статусами — кнопка «Детали» в журнале.
app.MapGet("/api/orders/{id:long}/csv-full", (long id, Db d) =>
{
    using var c = d.Open();
    var order = c.QueryFirstOrDefault<Order>("SELECT * FROM Orders WHERE Id=@id", new { id });
    if (order == null) return Results.NotFound();
    var report = c.QueryFirstOrDefault<OrderReport>("SELECT * FROM OrderReports WHERE OrderId=@id", new { id });
    var csv = report?.FullCsv;
    if (csv == null)
    {
        var lines = c.Query<OrderLine>("SELECT * FROM OrderLines WHERE OrderId=@id ORDER BY LineNo", new { id }).ToList();
        var scans = c.Query<ScannedItem>("SELECT * FROM ScannedItems WHERE OrderId=@id ORDER BY Id", new { id }).ToList();
        csv = BuildFullCsv(order, lines, scans);
        c.Execute("INSERT INTO OrderReports(OrderId,FullCsv,CreatedAt) VALUES(@id,@f,@n) " +
                  "ON CONFLICT(OrderId) DO UPDATE SET FullCsv=excluded.FullCsv", new { id, f = csv, n = Now() });
    }
    var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
    var name = $"Подробный_отчёт_{(order.OrderNumber ?? order.Id.ToString()).Replace('/', '-')}.csv";
    return Results.File(bytes, "text/csv; charset=utf-8", name);
});

static string StatusRu(string s) => s switch
{
    "ok" => "Проверен",
    "pending" => "В обработке",
    "error" => "Ошибка",
    "setaside" => "Отложен",
    _ => s
};

// Простой CSV для оператора: только коды маркировки успешных сканов, по одному в строке, без шапок/статусов.
static string BuildSimpleCsv(List<ScannedItem> scans)
{
    var sb = new StringBuilder();
    foreach (var s in scans.Where(s => s.ValidationStatus == "ok"))
        sb.AppendLine(s.DataMatrix);
    return sb.ToString();
}

// Подробный CSV: сводка по строкам ордера + все коды со статусами и временем.
static string BuildFullCsv(Order order, List<OrderLine> lines, List<ScannedItem> scans)
{
    var ruCulture = CultureInfo.GetCultureInfo("ru-RU");
    var sb = new StringBuilder();
    string E(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    sb.AppendLine($"Отчёт о проверке расходного ордера;{E(order.OrderNumber)}");
    sb.AppendLine($"Получатель;{E(order.Recipient)}");
    sb.AppendLine($"Склад;{E(order.Warehouse)}");
    sb.AppendLine($"Начало проверки;{E(order.CreatedAt)};Завершено;{E(order.ClosedAt)}");
    sb.AppendLine();
    sb.AppendLine("СВОДКА");
    sb.AppendLine("№;Код 1С;Наименование;Кол-во по ордеру;Проверено;Расхождение");
    foreach (var l in lines)
    {
        int ok = scans.Count(s => s.OrderLineId == l.Id && s.ValidationStatus == "ok");
        sb.AppendLine($"{l.LineNo};{E(l.Code1C)};{E(l.Name)};{l.Qty.ToString("0.###", ruCulture)};{ok};{(ok - l.Qty).ToString("0.###", ruCulture)}");
    }
    sb.AppendLine();
    sb.AppendLine("КОДЫ МАРКИРОВКИ");
    sb.AppendLine("Время;Код 1С;Наименование;GTIN;Серийный номер;Статус;Сообщение;Отложен;Полный код");
    foreach (var s in scans)
        sb.AppendLine(string.Join(";", new[]
        {
            E(s.CreatedAt), E(s.Code1C), E(s.ProductName), E(s.Gtin), E(s.Serial),
            E(StatusRu(s.ValidationStatus)), E(s.ValidationMessage), s.SetAside ? "да" : "нет", E(s.DataMatrix)
        }));
    return sb.ToString();
}

// ---------------------------------------------------------------- Настройки
app.MapGet("/api/settings", (Db d, ScannerService sc) =>
{
    var all = d.GetAllSettings();
    if (all.TryGetValue("cz_token", out var t) && !string.IsNullOrEmpty(t))
        all["cz_token_masked"] = t.Length > 12 ? t.Substring(0, 6) + "…" + t.Substring(t.Length - 4) : "задан";
    all.Remove("cz_token");
    all["bitrix_webhook_set"] = string.IsNullOrEmpty(all.GetValueOrDefault("bitrix_webhook")) ? "0" : "1";
    all.Remove("bitrix_webhook");
    return Results.Json(new { settings = all, ports = ScannerService.AvailablePorts(), scanner = sc.State });
});

app.MapPost("/api/settings", (Dictionary<string, string> body, Db d, ScannerService sc, SecretProtector sp) =>
{
    foreach (var kv in body)
    {
        if ((kv.Key == "cz_token" || kv.Key == "bitrix_webhook") && string.IsNullOrWhiteSpace(kv.Value)) continue; // не стираем секрет пустым значением
        if (kv.Key == "bitrix_webhook") { d.SetSetting(kv.Key, sp.Encrypt(kv.Value.Trim())); continue; }
        d.SetSetting(kv.Key, kv.Value);
    }
    sc.Reconnect();
    return Results.Ok(new { ok = true });
});

// Проверка связи с ЧЗ
app.MapPost("/api/cz/test", async (CzTestRequest r, TrueApiClient cz) =>
{
    var res = await cz.CheckAsync(string.IsNullOrWhiteSpace(r.Cis) ? "0104600000000000212TESTCODE" : r.Cis);
    return Results.Json(new { ok = res.Ok, skipped = res.Skipped, message = res.Message, raw = res.Raw });
});

// Тестовое сообщение в Битрикс24
app.MapPost("/api/bitrix/test", async (BitrixTestRequest r, BitrixNotifier bx) =>
{
    var dialog = string.IsNullOrWhiteSpace(r.DialogId) ? bx.Rules().Select(x => x.DialogId).FirstOrDefault() : r.DialogId.Trim();
    if (dialog == null) return Results.Json(new { ok = false, message = "Не задано ни одного правила «папка = диалог»" });
    var err = await bx.SendAsync(dialog, "Тест MarkCheck: связь с Битрикс24 работает");
    return Results.Json(new { ok = err == null, message = err ?? $"Отправлено в {dialog}" });
});

// ---------------------------------------------------------------- Опасная зона: очистка базы
// Перед любым удалением создаётся копия всего файла БД в data/delete-<время>.db —
// на случай, если очистка была ошибочной. Подтверждение словом УДАЛИТЬ проверяется на фронте.
app.MapPost("/api/admin/clear-journal", (Db d) =>
{
    var backupPath = Path.Combine(dataDir, $"delete-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.db");
    d.BackupTo(backupPath);
    using var c = d.Open();
    c.Execute("DELETE FROM ScannedItems; DELETE FROM OrderLines; DELETE FROM Orders; DELETE FROM OrderReports;");
    return Results.Json(new { ok = true, backup = Path.GetFileName(backupPath) });
});

app.MapPost("/api/admin/clear-nomenclature", (Db d) =>
{
    var backupPath = Path.Combine(dataDir, $"delete-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.db");
    d.BackupTo(backupPath);
    using var c = d.Open();
    c.Execute("DELETE FROM Products; DELETE FROM Barcodes;");
    d.SetSetting("barcodes_stamp", "");
    return Results.Json(new { ok = true, backup = Path.GetFileName(backupPath) });
});

// Создаёт ордер из печатной формы и сопоставляет строки с базой ШК.
object CreateOrder(Microsoft.Data.Sqlite.SqliteConnection c, Stream stream, string fileName, string sourcePath)
{
    var parsed = OrderParser.Parse(stream, out var warnings);
    SkuResolver.Resolve(c, parsed.Lines, parsed.ByArticle);
    var id = c.ExecuteScalar<long>(@"
INSERT INTO Orders(OrderNumber,FileName,Recipient,Warehouse,CreatedAt,Status,DocumentJson,SourcePath,Instruction,ShipmentType)
VALUES(@num,@file,@rec,@wh,@now,'active',@json,@src,@ins,'FBO'); SELECT last_insert_rowid();",
        new
        {
            num = parsed.OrderNumber, file = fileName, rec = parsed.Recipient, wh = parsed.Warehouse,
            now = Now(), json = JsonSerializer.Serialize(parsed), src = sourcePath, ins = parsed.Instruction
        });
    foreach (var l in parsed.Lines)
        c.Execute(@"INSERT INTO OrderLines(OrderId,LineNo,Code1C,Article,Name,Qty,Unit,RequiresMarking,SkuKey,MatchStatus,MatchInfo)
                    VALUES(@o,@LineNo,@Code1C,@Article,@Name,@Qty,@Unit,@rm,@SkuKey,@MatchStatus,@MatchInfo)",
            new { o = id, l.LineNo, l.Code1C, l.Article, l.Name, l.Qty, l.Unit, rm = l.RequiresMarking ? 1 : 0, l.SkuKey, l.MatchStatus, l.MatchInfo });
    int bad = parsed.Lines.Count(l => l.MatchStatus != "ok");
    if (bad > 0) warnings.Insert(0, $"Не сопоставлено с базой ШК строк: {bad} — проверьте базу ШК (отмечены красным)");
    return new { orderId = id, orderNumber = parsed.OrderNumber, lines = parsed.Lines.Count, warnings };
}

// Закрывает ордер и сохраняет отчёты в базе (кнопки скачивания работают повторно).
void CloseOrder(Microsoft.Data.Sqlite.SqliteConnection c, long id)
{
    c.Execute("UPDATE Orders SET Status='closed', ClosedAt=COALESCE(ClosedAt,@n) WHERE Id=@id", new { n = Now(), id });
    var order = c.QueryFirstOrDefault<Order>("SELECT * FROM Orders WHERE Id=@id", new { id });
    var lines = c.Query<OrderLine>("SELECT * FROM OrderLines WHERE OrderId=@id ORDER BY LineNo", new { id }).ToList();
    var scans = c.Query<ScannedItem>("SELECT * FROM ScannedItems WHERE OrderId=@id ORDER BY Id", new { id }).ToList();
    c.Execute("INSERT INTO OrderReports(OrderId,SimpleCsv,FullCsv,CreatedAt) VALUES(@id,@s,@f,@n) " +
              "ON CONFLICT(OrderId) DO UPDATE SET SimpleCsv=excluded.SimpleCsv, FullCsv=excluded.FullCsv, CreatedAt=excluded.CreatedAt",
              new { id, s = BuildSimpleCsv(scans), f = BuildFullCsv(order, lines, scans), n = Now() });
}

Console.WriteLine();
Console.WriteLine("=== Станция проверки кодов маркировки (MVP) ===");
Console.WriteLine($"Веб-интерфейс: {url.Replace("0.0.0.0", "localhost")}");
Console.WriteLine($"База данных:   {Path.Combine(dataDir, "markcheck.db")}");
Console.WriteLine("Для остановки нажмите Ctrl+C");
Console.WriteLine();

app.Run();

class LineCount { public long? OrderLineId { get; set; } public int Cnt { get; set; } }
record ScanRequest(string Code);
record OpenRequest(string Path, bool Force);
record FinishRequest(int Boxes);
record BitrixTestRequest(string DialogId);
record CzTestRequest(string Cis);
