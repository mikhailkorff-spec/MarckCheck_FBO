using System.Text.RegularExpressions;

namespace MarkCheck.Services;

/// <summary>
/// Единая нормализация текста для сравнения наименований и характеристик из печатных форм 1С и отчёта по ШК.
/// Сравнение после нормализации — точное (без «похожих» совпадений), чтобы не порождать пересортицу.
/// </summary>
public static class NameKey
{
    private static readonly Regex Quotes = new("[«»“”„\"]", RegexOptions.Compiled);
    private static readonly Regex Slashes = new(@"\s*[\\/]+\s*", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Dashes = new("[–—−]", RegexOptions.Compiled);

    /// <summary>
    /// Нижний регистр, ё→е, неразрывные пробелы, единые кавычки и тире,
    /// любые слеши (\, \\, /, //) с пробелами вокруг → «/», звёздочки в начале убираются.
    /// </summary>
    public static string Norm(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var t = s.Replace('\u00a0', ' ').Trim().ToLowerInvariant().Replace('ё', 'е');
        t = Quotes.Replace(t, "\"");
        t = Dashes.Replace(t, "-");
        t = Slashes.Replace(t, "/");
        t = t.TrimStart('*', ' ');
        t = Spaces.Replace(t, " ");
        return t.Trim();
    }

    /// <summary>Ключ товарной позиции: код 1С + нормализованная характеристика.</summary>
    public static string Sku(string code1C, string characteristic) =>
        (code1C ?? "").Trim().ToUpperInvariant() + "|" + Norm(characteristic);

    /// <summary>
    /// Приводит значение колонки «Штрихкод» к 14-значному GTIN. Поддерживаются: 8–14 цифр,
    /// GS1-строка «(01)…(10)…», суффиксы «*»/«**». Null — если GTIN не распознан.
    /// </summary>
    public static string GtinFromBarcode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var m = Regex.Match(s, @"\(01\)(\d{14})");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(s, @"\d{8,14}");
        return m.Success ? m.Value.PadLeft(14, '0') : null;
    }
}
