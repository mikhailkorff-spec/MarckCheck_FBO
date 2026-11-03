using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MarkCheck.Data;

namespace MarkCheck.Services;

public class ParsedOrder
{
    public string OrderNumber { get; set; }
    public string Recipient { get; set; }
    public string Warehouse { get; set; }
    public List<OrderLine> Lines { get; set; } = new();
}

/// <summary>
/// Разбор печатной формы «Расходный ордер на товары» из 1С (.xlsx).
/// Логика устойчива к объединённым ячейкам и сдвигу колонок:
/// колонки шапки («Код», «Товар», «Количество») определяются по заголовкам,
/// значения строк распределяются по диапазонам этих колонок.
/// </summary>
public static class OrderParser
{
    public static ParsedOrder Parse(Stream stream, out List<string> warnings)
    {
        warnings = new List<string>();
        var res = new ParsedOrder();
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();

        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;

        // ---- 1. Шапка документа ----
        for (int r = 1; r <= Math.Min(lastRow, 30); r++)
        {
            var cells = RowCells(ws, r, lastCol);
            foreach (var (col, val) in cells)
            {
                if (res.OrderNumber == null && val.Contains("Расходный ордер", StringComparison.OrdinalIgnoreCase))
                {
                    var m = Regex.Match(val, @"№\s*([^\s]+)");
                    res.OrderNumber = m.Success ? m.Groups[1].Value : val;
                }
                if (val.StartsWith("Получатель", StringComparison.OrdinalIgnoreCase))
                    res.Recipient = NextValue(cells, col);
                if (val.StartsWith("Склад", StringComparison.OrdinalIgnoreCase))
                    res.Warehouse = NextValue(cells, col);
            }
        }

        // ---- 2. Поиск строки заголовков таблицы ----
        int headerRow = -1;
        var headers = new List<(int Col, string Text)>();
        for (int r = 1; r <= lastRow; r++)
        {
            var cells = RowCells(ws, r, lastCol);
            bool hasCode = cells.Any(c => Eq(c.Val, "Код"));
            bool hasName = cells.Any(c => Eq(c.Val, "Товар") || Eq(c.Val, "Номенклатура"));
            bool hasQty = cells.Any(c => c.Val.Contains("Количество", StringComparison.OrdinalIgnoreCase));
            if (hasCode && hasName && hasQty)
            {
                headerRow = r;
                headers = cells.Select(c => (c.Col, Norm(c.Val))).ToList();
                break;
            }
        }
        if (headerRow < 0)
            throw new InvalidOperationException("Не найдена таблица товаров: нет строки с заголовками «Код», «Товар», «Количество».");

        // Индексы колонок
        int colCode = headers.First(h => Eq(h.Text, "Код")).Col;
        int colName = headers.First(h => Eq(h.Text, "Товар") || Eq(h.Text, "Номенклатура")).Col;
        var qtyCols = headers.Where(h => h.Text.Contains("Количество", StringComparison.OrdinalIgnoreCase)).ToList();
        // «Количество» в базовых единицах приоритетнее, чем «Количество (упаковок)»
        var qtyHeader = qtyCols.FirstOrDefault(h => !h.Text.Contains("упаков", StringComparison.OrdinalIgnoreCase));
        if (qtyHeader.Col == 0) qtyHeader = qtyCols.Last();
        int colQty = qtyHeader.Col;

        var bounds = headers.Select(h => h.Col).OrderBy(x => x).ToList();

        // ---- 3. Строки товаров ----
        int lineNo = 0;
        for (int r = headerRow + 1; r <= lastRow; r++)
        {
            var cells = RowCells(ws, r, lastCol);
            if (cells.Count == 0) continue;
            var firstVal = cells[0].Val;
            if (firstVal.StartsWith("Всего", StringComparison.OrdinalIgnoreCase) ||
                firstVal.StartsWith("Отпустил", StringComparison.OrdinalIgnoreCase) ||
                firstVal.StartsWith("Итого", StringComparison.OrdinalIgnoreCase)) break;

            var codeBucket = Bucket(cells, bounds, colCode);
            var nameBucket = Bucket(cells, bounds, colName);
            var qtyBucket = Bucket(cells, bounds, colQty);

            string code = codeBucket.FirstOrDefault();
            string name = nameBucket.FirstOrDefault(v => !IsMarkFlag(v));
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) continue;

            double qty = 0;
            string unit = null;
            foreach (var v in qtyBucket)
            {
                if (TryNum(v, out var d)) { if (qty == 0) qty = d; }
                else if (unit == null) unit = v;
            }
            if (qty <= 0) warnings.Add($"Строка {r}: не удалось определить количество для «{code}».");

            lineNo++;
            res.Lines.Add(new OrderLine
            {
                LineNo = lineNo,
                Code1C = code.Trim(),
                Name = name.Trim(),
                Qty = qty,
                Unit = unit,
                RequiresMarking = nameBucket.Any(IsMarkFlag) || cells.Any(c => IsMarkFlag(c.Val))
            });
        }

        if (res.Lines.Count == 0)
            throw new InvalidOperationException("В файле не найдено ни одной строки товара.");
        return res;
    }

    private static bool IsMarkFlag(string v) =>
        v != null && (v.Trim() == "[М]" || v.Trim() == "[M]");

    private static List<(int Col, string Val)> RowCells(IXLWorksheet ws, int r, int lastCol)
    {
        var list = new List<(int, string)>();
        for (int c = 1; c <= lastCol; c++)
        {
            var cell = ws.Cell(r, c);
            var s = cell.GetFormattedString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            list.Add((c, s.Replace('\u00a0', ' ').Trim()));
        }
        return list;
    }

    /// <summary>Значения строки, попадающие в диапазон колонки заголовка.</summary>
    private static List<string> Bucket(List<(int Col, string Val)> cells, List<int> bounds, int headerCol)
    {
        int idx = bounds.IndexOf(headerCol);
        int start = headerCol;
        int end = idx >= 0 && idx + 1 < bounds.Count ? bounds[idx + 1] : int.MaxValue;
        return cells.Where(c => c.Col >= start && c.Col < end).Select(c => c.Val).ToList();
    }

    private static string NextValue(List<(int Col, string Val)> cells, int afterCol) =>
        cells.Where(c => c.Col > afterCol).Select(c => c.Val).FirstOrDefault();

    private static bool Eq(string a, string b) =>
        Norm(a).Equals(Norm(b), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string s) =>
        (s ?? "").Replace("\n", " ").Replace('\u00a0', ' ').Replace(":", "").Trim();

    private static bool TryNum(string s, out double d)
    {
        s = (s ?? "").Replace(" ", "").Replace(',', '.');
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d);
    }
}
