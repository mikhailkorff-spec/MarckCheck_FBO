using Dapper;
using Microsoft.Data.Sqlite;
using MarkCheck.Data;

namespace MarkCheck.Services;

/// <summary>
/// Сопоставление строк расходного ордера с позициями базы ШК (номенклатура + характеристика).
/// Выполняется один раз при открытии ордера; при сканировании GTIN → SkuKey → строка ордера.
/// Колонка «Товар» в печатной форме = «Наименование для печати (Характеристика для печати или серия)».
/// </summary>
public static class SkuResolver
{
    private class Sku
    {
        public string SkuKey { get; set; }
        public string Code1C { get; set; }
        public string Name { get; set; }
        public string Characteristic { get; set; }
        public string PrintName { get; set; }
        public string PrintCharacteristic { get; set; }
        public string Article { get; set; }
    }

    /// <summary>Заполняет у строк SkuKey, MatchStatus (ok/none/amb), MatchInfo и Code1C.</summary>
    public static void Resolve(SqliteConnection c, List<OrderLine> lines, bool byArticle)
    {
        var all = c.Query<Sku>(@"SELECT SkuKey, MIN(Code1C) Code1C, MIN(Name) Name, MIN(Characteristic) Characteristic,
            MIN(PrintName) PrintName, MIN(PrintCharacteristic) PrintCharacteristic, MIN(Article) Article
            FROM Barcodes GROUP BY SkuKey").ToList();

        foreach (var l in lines)
        {
            var key = byArticle ? NameKey.Norm(l.Article) : (l.Code1C ?? "").Trim().ToUpperInvariant();
            var cand = all.Where(s => byArticle
                    ? NameKey.Norm(s.Article) == key && key.Length > 0
                    : (s.Code1C ?? "").Trim().ToUpperInvariant() == key).ToList();
            var name = NameKey.Norm(l.Name);

            List<Sku> found;
            if (cand.Count == 0)
            {
                // Нет кода/артикула в базе — пробуем по полному наименованию для печати.
                found = all.Where(s => name == Full(s.PrintName, s.PrintCharacteristic) || name == Full(s.Name, s.Characteristic)).ToList();
            }
            else if (cand.Select(s => s.SkuKey).Distinct().Count() == 1 && string.IsNullOrWhiteSpace(cand[0].Characteristic))
            {
                found = cand; // товар без характеристик: в скобках может быть серия — её игнорируем
            }
            else
            {
                found = cand.Where(s =>
                    (EndsChar(name, s.PrintCharacteristic) || EndsChar(name, s.Characteristic)) &&
                    (!byArticle || name.StartsWith(NameKey.Norm(s.PrintName)) || name.StartsWith(NameKey.Norm(s.Name)))).ToList();
                if (found.Count == 0)
                    found = cand.Where(s => string.IsNullOrWhiteSpace(s.Characteristic)).ToList();
            }

            var keys = found.Select(s => s.SkuKey).Distinct().ToList();
            if (keys.Count == 1)
            {
                l.SkuKey = keys[0]; l.MatchStatus = "ok"; l.Code1C ??= found[0].Code1C;
                l.MatchInfo = found[0].Code1C + (string.IsNullOrWhiteSpace(found[0].Characteristic) ? "" : " / " + found[0].Characteristic.Trim());
            }
            else if (keys.Count > 1)
            {
                l.MatchStatus = "amb";
                l.MatchInfo = "Несколько вариантов в базе ШК: " + string.Join("; ", found.Select(s => s.Code1C + " " + s.Characteristic?.Trim()).Distinct());
            }
            else
            {
                l.MatchStatus = "none";
                l.MatchInfo = cand.Count == 0 ? "Код/артикул не найден в базе ШК" : "Характеристика не найдена в базе ШК";
            }
        }
    }

    private static string Full(string n, string ch) =>
        string.IsNullOrWhiteSpace(ch) ? NameKey.Norm(n) : NameKey.Norm(n) + " (" + NameKey.Norm(ch) + ")";

    private static bool EndsChar(string name, string ch)
    {
        var n = NameKey.Norm(ch);
        return n.Length > 0 && name.EndsWith("(" + n + ")");
    }
}
