using Dapper;
using MarkCheck.Data;

namespace MarkCheck.Services;

/// <summary>
/// База штрихкодов (отчёт по ШК из 1С): полная замена при загрузке и пересопоставление открытого ордера.
/// </summary>
public static class BarcodeStore
{
    public static int Replace(Db db, List<BarcodeRow> rows)
    {
        using var c = db.Open();
        using (var tx = c.BeginTransaction())
        {
            c.Execute("DELETE FROM Barcodes", transaction: tx);
            c.Execute(@"INSERT INTO Barcodes(Gtin,RawBarcode,Code1C,Name,Characteristic,PrintName,PrintCharacteristic,Article,SkuKey)
                        VALUES(@Gtin,@RawBarcode,@Code1C,@Name,@Characteristic,@PrintName,@PrintCharacteristic,@Article,@SkuKey)", rows, tx);
            tx.Commit();
        }
        ResolveActiveOrder(db);
        return rows.Count;
    }

    /// <summary>Пересопоставляет строки открытого ордера (после обновления базы ШК).</summary>
    public static void ResolveActiveOrder(Db db)
    {
        using var c = db.Open();
        var id = c.ExecuteScalar<long?>("SELECT Id FROM Orders WHERE Status='active' ORDER BY Id DESC LIMIT 1");
        if (id == null) return;
        var lines = c.Query<OrderLine>("SELECT * FROM OrderLines WHERE OrderId=@id", new { id }).ToList();
        bool byArticle = lines.Any(l => !string.IsNullOrEmpty(l.Article));
        if (byArticle) foreach (var l in lines) l.Code1C = null;
        SkuResolver.Resolve(c, lines, byArticle);
        foreach (var l in lines)
            c.Execute("UPDATE OrderLines SET SkuKey=@SkuKey, MatchStatus=@MatchStatus, MatchInfo=@MatchInfo, Code1C=@Code1C WHERE Id=@Id", l);
    }
}

/// <summary>
/// Автозагрузка базы ШК из папки/файла (настройка barcodes_path): раз в минуту проверяет самый свежий .xlsx
/// и загружает его, если файл изменился. Удобно класть отчёт из 1С в ту же общую папку.
/// </summary>
public class BarcodeSyncService : BackgroundService
{
    private readonly Db _db;
    private readonly ILogger<BarcodeSyncService> _log;
    public string LastResult { get; private set; } = "";

    public BarcodeSyncService(Db db, ILogger<BarcodeSyncService> log) { _db = db; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { SyncNow(false); } catch (Exception ex) { LastResult = "Ошибка: " + ex.Message; _log.LogWarning(ex, "Автозагрузка ШК"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch { }
        }
    }

    /// <summary>Загружает файл, если он изменился (или всегда при force). Возвращает текст результата.</summary>
    public string SyncNow(bool force)
    {
        var path = (_db.GetSetting("barcodes_path", "") ?? "").Trim();
        if (path.Length == 0) return LastResult = "Автозагрузка не настроена";
        string file = path;
        if (Directory.Exists(path))
            file = new DirectoryInfo(path).GetFiles("*.xlsx").Where(f => !f.Name.StartsWith("~$"))
                       .OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
        if (file == null || !File.Exists(file)) return LastResult = "Файл базы ШК не найден: " + path;
        var stamp = file + "|" + File.GetLastWriteTime(file).ToString("s");
        if (!force && _db.GetSetting("barcodes_stamp") == stamp) return LastResult;
        using var ms = new MemoryStream();
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) fs.CopyTo(ms);
        ms.Position = 0;
        var rows = BarcodeImportParser.Parse(ms, out var warnings);
        if (rows.Count == 0) return LastResult = "Файл без строк: " + file;
        BarcodeStore.Replace(_db, rows);
        _db.SetSetting("barcodes_stamp", stamp);
        LastResult = $"{DateTime.Now:dd.MM HH:mm} загружено {rows.Count} ШК из «{Path.GetFileName(file)}», предупреждений: {warnings.Count}";
        _db.SetSetting("barcodes_last", LastResult);
        return LastResult;
    }
}
