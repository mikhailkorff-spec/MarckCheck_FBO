using ClosedXML.Excel;

namespace MarkCheck.Services;

/// <summary>Строка базы штрихкодов (отчёт по ШК номенклатуры из 1С).</summary>
public class BarcodeRow
{
    public string Gtin { get; set; }
    public string RawBarcode { get; set; }
    public string Code1C { get; set; }
    public string Name { get; set; }
    public string Characteristic { get; set; }
    public string PrintName { get; set; }
    public string PrintCharacteristic { get; set; }
    public string Article { get; set; }
    public string SkuKey { get; set; }
}

/// <summary>
/// Разбор отчёта по ШК номенклатуры (.xlsx). Колонки ищутся по заголовкам:
/// «Штрихкод», «Номенклатура.Код», «Номенклатура.Наименование», «Характеристика.Наименование»,
/// «Номенклатура.Наименование для печати», «Характеристика.Наименование для печати», «Номенклатура.Артикул».
/// Обязательны только первые три. Один товар может иметь несколько штрихкодов, и один GTIN —
/// несколько номенклатур (дубли в 1С): это решается при сканировании по составу открытого ордера.
/// </summary>
public static class BarcodeImportParser
{
    public static List<BarcodeRow> Parse(Stream stream, out List<string> warnings)
    {
        warnings = new List<string>();
        var result = new List<BarcodeRow>();
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;

        int header = -1;
        var col = new Dictionary<string, int>();
        for (int r = 1; r <= Math.Min(lastRow, 20) && header < 0; r++)
        {
            var map = new Dictionary<string, int>();
            for (int c = 1; c <= lastCol; c++)
            {
                var v = ws.Cell(r, c).GetString().Replace('\u00a0', ' ').Trim().ToLowerInvariant();
                string key = v switch
                {
                    "штрихкод" => "bc",
                    "номенклатура.код" or "код" => "code",
                    "номенклатура.наименование" or "наименование" => "name",
                    "характеристика.наименование" or "характеристика" => "char",
                    "номенклатура.наименование для печати" => "pname",
                    "характеристика.наименование для печати" => "pchar",
                    "номенклатура.артикул" or "артикул" => "art",
                    _ => null
                };
                if (key != null && !map.ContainsKey(key)) map[key] = c;
            }
            if (map.ContainsKey("bc") && map.ContainsKey("code") && (map.ContainsKey("name") || map.ContainsKey("pname")))
            { header = r; col = map; }
        }
        if (header < 0)
            throw new InvalidOperationException("Не найдены заголовки «Штрихкод», «Номенклатура.Код», «Номенклатура.Наименование».");

        string Get(int r, string k) => col.TryGetValue(k, out var c) ? ws.Cell(r, c).GetString().Replace('\u00a0', ' ').Trim() : "";

        for (int r = header + 1; r <= lastRow; r++)
        {
            var raw = Get(r, "bc");
            var code = Get(r, "code");
            if (raw.Length == 0 && code.Length == 0) continue;
            var gtin = NameKey.GtinFromBarcode(raw);
            if (gtin == null) { warnings.Add($"Строка {r}: не распознан штрихкод «{raw}»"); continue; }
            if (code.Length == 0) { warnings.Add($"Строка {r}: не заполнен код номенклатуры"); continue; }
            var name = Get(r, "name");
            var ch = Get(r, "char");
            var pname = Get(r, "pname");
            var pch = Get(r, "pchar");
            result.Add(new BarcodeRow
            {
                Gtin = gtin, RawBarcode = raw, Code1C = code,
                Name = name.Length > 0 ? name : pname,
                Characteristic = ch,
                PrintName = pname.Length > 0 ? pname : name,
                PrintCharacteristic = pch.Length > 0 ? pch : ch,
                Article = Get(r, "art"),
                SkuKey = NameKey.Sku(code, ch)
            });
        }
        return result;
    }
}
