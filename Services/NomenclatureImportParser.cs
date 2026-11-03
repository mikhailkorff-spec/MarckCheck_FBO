using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace MarkCheck.Services;

/// <summary>Одна распознанная строка справочника номенклатуры из файла импорта.</summary>
public class NomenclatureImportRow
{
    public string Gtin { get; set; }
    public string Code1C { get; set; }
    public string Name { get; set; }
}

/// <summary>
/// Разбор справочника номенклатуры из .xlsx по шаблону выгрузки 1С:
/// колонки «Штрихкод», «Номенклатура.Код», «Номенклатура.Наименование для печати»,
/// «Характеристика.Наименование для печати» (также понимаются старый вариант без «для печати»).
/// Штрихкод может быть представлен несколькими форматами (см. GtinFromBarcode).
/// Одному Коду1С может соответствовать несколько строк-штрихкодов — каждая формирует отдельную запись.
/// </summary>
public static class NomenclatureImportParser
{
    public static List<NomenclatureImportRow> Parse(Stream stream, out List<string> warnings)
    {
        warnings = new List<string>();
        var result = new List<NomenclatureImportRow>();

        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();

        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (lastRow == 0) { warnings.Add("Файл пуст"); return result; }

        // ---- Поиск строки заголовков по названиям колонок ----
        int headerRow = -1;
        int colBarcode = -1, colCode = -1, colName = -1, colChar = -1;
        for (int r = 1; r <= lastRow; r++)
        {
            int bc = -1, cc = -1, nc = -1, chc = -1;
            for (int c = 1; c <= lastCol; c++)
            {
                var v = ws.Cell(r, c).GetString().Trim();
                if (v.Length == 0) continue;
                if (Eq(v, "Штрихкод")) bc = c;
                else if (Eq(v, "Номенклатура.Код") || Eq(v, "Код")) cc = c;
                else if (Eq(v, "Номенклатура.Наименование для печати") || Eq(v, "Номенклатура.Наименование") || Eq(v, "Наименование для печати") || Eq(v, "Наименование")) nc = c;
                else if (Eq(v, "Характеристика.Наименование для печати") || Eq(v, "Характеристика.Наименование") || Eq(v, "Характеристика")) chc = c;
            }
            if (bc > 0 && cc > 0 && nc > 0)
            {
                headerRow = r; colBarcode = bc; colCode = cc; colName = nc; colChar = chc;
                break;
            }
        }

        if (headerRow == -1)
        {
            warnings.Add("Не найдена строка заголовков (ожидались колонки «Штрихкод», «Номенклатура.Код», «Номенклатура.Наименование»)");
            return result;
        }

        for (int r = headerRow + 1; r <= lastRow; r++)
        {
            var barcodeRaw = ws.Cell(r, colBarcode).GetString().Trim();
            var code1c = ws.Cell(r, colCode).GetString().Trim();
            var name = ws.Cell(r, colName).GetString().Trim();
            var characteristic = colChar > 0 ? ws.Cell(r, colChar).GetString().Trim() : "";

            if (barcodeRaw.Length == 0 && code1c.Length == 0 && name.Length == 0) continue; // пустая строка
            if (barcodeRaw.Length == 0)
            {
                warnings.Add($"Строка {r}: пропущена — не заполнен штрихкод");
                continue;
            }
            if (code1c.Length == 0 || name.Length == 0)
            {
                warnings.Add($"Строка {r}: пропущена — не заполнен код 1С или наименование");
                continue;
            }

            var gtin = GtinFromBarcode(barcodeRaw);
            if (string.IsNullOrEmpty(gtin))
            {
                warnings.Add($"Строка {r}: не удалось распознать GTIN из штрихкода «{barcodeRaw}»");
                continue;
            }

            var fullName = characteristic.Length > 0 ? $"{name} ({characteristic})" : name;
            result.Add(new NomenclatureImportRow { Gtin = gtin, Code1C = code1c, Name = fullName });
        }

        return result;
    }

    /// <summary>
    /// Извлекает GTIN из значения колонки «Штрихкод». Поддерживаемые форматы:
    /// - чистый цифровой код (13 или 14 знаков) — используется как есть;
    /// - GS1 DataMatrix с идентификаторами применения, напр. (01)04603764218092(10)HH2117960701 — берём 14 цифр после (01);
    /// - код с суффиксом "**" (напр. 2039429774979**) — суффикс отбрасывается, остаток используется как есть.
    /// </summary>
    public static string GtinFromBarcode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        var m = Regex.Match(s, @"\(01\)(\d{14})");
        if (m.Success) return m.Groups[1].Value;

        if (s.EndsWith("**")) s = s[..^2];

        s = s.Trim();
        if (Regex.IsMatch(s, @"^\d{8,14}$")) return s;

        // На всякий случай — вытащить любую цифровую последовательность длиной 8-14 из строки.
        var digits = Regex.Match(s, @"\d{8,14}");
        return digits.Success ? digits.Value : null;
    }

    private static bool Eq(string a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);
}
