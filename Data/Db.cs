using Dapper;
using Microsoft.Data.Sqlite;

namespace MarkCheck.Data;

/// <summary>Доступ к SQLite: создание схемы, соединения, настройки.</summary>
public class Db
{
    private readonly string _connStr;

    /// <summary>Полный путь к файлу базы данных на диске.</summary>
    public string DbPath { get; }

    public Db(string dbPath)
    {
        DbPath = dbPath;
        _connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    /// <summary>
    /// Создаёт копию файла БД по указанному пути. Перед копированием выполняет WAL-чекпоинт,
    /// чтобы в копию попали все данные из журнала предварительной записи.
    /// </summary>
    public void BackupTo(string destPath)
    {
        using (var c = Open())
            c.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        File.Copy(DbPath, destPath, overwrite: true);
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        c.Execute("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");
        return c;
    }

    /// <summary>Создаёт таблицы при первом запуске.</summary>
    public void Init()
    {
        using var c = Open();
        c.Execute(@"
CREATE TABLE IF NOT EXISTS Products (
    Id      INTEGER PRIMARY KEY AUTOINCREMENT,
    Code1C  TEXT NOT NULL,
    Name    TEXT NOT NULL,
    Gtin    TEXT NOT NULL,
    Marked  INTEGER NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Products_Gtin ON Products(Gtin);

CREATE TABLE IF NOT EXISTS Orders (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    OrderNumber  TEXT,
    FileName     TEXT,
    Recipient    TEXT,
    Warehouse    TEXT,
    CreatedAt    TEXT NOT NULL,
    ClosedAt     TEXT,
    Status       TEXT NOT NULL DEFAULT 'active',
    DocumentJson TEXT
);

CREATE TABLE IF NOT EXISTS OrderLines (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    OrderId         INTEGER NOT NULL REFERENCES Orders(Id) ON DELETE CASCADE,
    LineNo          INTEGER NOT NULL,
    Code1C          TEXT,
    Name            TEXT,
    Qty             REAL NOT NULL DEFAULT 0,
    Unit            TEXT,
    RequiresMarking INTEGER NOT NULL DEFAULT 1,
    Gtin            TEXT
);
CREATE INDEX IF NOT EXISTS IX_OrderLines_Order ON OrderLines(OrderId);

CREATE TABLE IF NOT EXISTS ScannedItems (
    Id                INTEGER PRIMARY KEY AUTOINCREMENT,
    OrderId           INTEGER NOT NULL REFERENCES Orders(Id) ON DELETE CASCADE,
    OrderLineId       INTEGER,
    ProductId         INTEGER,
    DataMatrix        TEXT NOT NULL,
    Cis               TEXT,
    Gtin              TEXT,
    Serial            TEXT,
    ValidationStatus  TEXT NOT NULL,
    ValidationMessage TEXT,
    ProductName       TEXT,
    Code1C            TEXT,
    IsMapped          INTEGER NOT NULL DEFAULT 0,
    SetAside          INTEGER NOT NULL DEFAULT 0,
    CreatedAt         TEXT NOT NULL,
    RawResponse       TEXT
);
CREATE INDEX IF NOT EXISTS IX_Scanned_Order ON ScannedItems(OrderId);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Scanned_Cis_Order ON ScannedItems(OrderId, Cis);
CREATE INDEX IF NOT EXISTS IX_Scanned_Cis ON ScannedItems(Cis);

CREATE TABLE IF NOT EXISTS Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT
);

CREATE TABLE IF NOT EXISTS OrderReports (
    OrderId   INTEGER PRIMARY KEY REFERENCES Orders(Id) ON DELETE CASCADE,
    SimpleCsv TEXT,
    FullCsv   TEXT,
    CreatedAt TEXT NOT NULL
);");

        c.Execute(@"
CREATE TABLE IF NOT EXISTS Barcodes (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    Gtin                TEXT NOT NULL,
    RawBarcode          TEXT,
    Code1C              TEXT NOT NULL,
    Name                TEXT,
    Characteristic      TEXT,
    PrintName           TEXT,
    PrintCharacteristic TEXT,
    Article             TEXT,
    SkuKey              TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Barcodes_Gtin ON Barcodes(Gtin);
CREATE INDEX IF NOT EXISTS IX_Barcodes_Code ON Barcodes(Code1C);");

        // Миграции: добавляем недостающие колонки в базы старых версий.
        AddColumn(c, "OrderLines", "Gtin", "TEXT");
        AddColumn(c, "OrderLines", "Article", "TEXT");
        AddColumn(c, "OrderLines", "SkuKey", "TEXT");
        AddColumn(c, "OrderLines", "MatchStatus", "TEXT");
        AddColumn(c, "OrderLines", "MatchInfo", "TEXT");
        AddColumn(c, "Orders", "SourcePath", "TEXT");
        AddColumn(c, "Orders", "Instruction", "TEXT");
        AddColumn(c, "Orders", "ShipmentType", "TEXT NOT NULL DEFAULT 'FBO'");
        AddColumn(c, "Orders", "Boxes", "INTEGER");
        AddColumn(c, "Orders", "FinishInfo", "TEXT");
    }

    private static void AddColumn(SqliteConnection c, string table, string col, string type)
    {
        var exists = c.Query($"PRAGMA table_info({table})")
            .Any(r => string.Equals((string)r.name, col, StringComparison.OrdinalIgnoreCase));
        if (!exists) c.Execute($"ALTER TABLE {table} ADD COLUMN {col} {type};");
    }

    public string GetSetting(string key, string def = null)
    {
        using var c = Open();
        return c.ExecuteScalar<string>("SELECT Value FROM Settings WHERE Key=@key", new { key }) ?? def;
    }

    public Dictionary<string, string> GetAllSettings()
    {
        using var c = Open();
        return c.Query<(string Key, string Value)>("SELECT Key, Value FROM Settings")
                .ToDictionary(x => x.Key, x => x.Value);
    }

    public void SetSetting(string key, string value)
    {
        using var c = Open();
        c.Execute("INSERT INTO Settings(Key,Value) VALUES(@key,@value) " +
                  "ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", new { key, value });
    }
}
