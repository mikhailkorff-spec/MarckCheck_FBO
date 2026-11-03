using System.Text;
using System.Text.RegularExpressions;

namespace MarkCheck.Services;

public class Gs1Code
{
    public bool IsDataMatrix { get; set; }
    public string Raw { get; set; }
    /// <summary>GTIN (14 цифр, AI 01).</summary>
    public string Gtin { get; set; }
    /// <summary>Серийный номер (AI 21).</summary>
    public string Serial { get; set; }
    /// <summary>КИ = 01+GTIN+21+Serial — то, что отправляем в ЧЗ.</summary>
    public string Cis { get; set; }
    public string Error { get; set; }
}

/// <summary>Разбор кода маркировки GS1 DataMatrix.</summary>
public static class Gs1Parser
{
    private const char GS = (char)29; // разделитель групп FNC1

    public static Gs1Code Parse(string raw)
    {
        var r = new Gs1Code { Raw = raw };
        if (string.IsNullOrWhiteSpace(raw)) { r.Error = "Пустой код"; return r; }

        var s = raw.Trim();
        // Сканер может отдавать GS как <GS>, \u001d, ]d2 или {GS}
        s = s.Replace("<GS>", GS.ToString())
             .Replace("{GS}", GS.ToString())
             .Replace("\u001d", GS.ToString())
             .Replace("\u241d", GS.ToString());
        if (s.StartsWith("]d2") || s.StartsWith("]C1") || s.StartsWith("]e0")) s = s.Substring(3);
        // Человекочитаемая запись со скобками: (01)...(21)...
        bool bracket = s.Contains("(01)");
        if (bracket) s = Regex.Replace(s, @"\((\d{2,4})\)", "$1");

        // GTIN
        var mGtin = Regex.Match(s, @"01(\d{14})");
        if (!mGtin.Success || mGtin.Index != 0)
        {
            r.IsDataMatrix = false;
            r.Error = "Код не распознан как GS1 DataMatrix (нет AI 01)";
            return r;
        }
        r.IsDataMatrix = true;
        r.Gtin = mGtin.Groups[1].Value;

        // Серийный номер после AI 21 — до GS либо до конца/следующего известного AI
        var rest = s.Substring(mGtin.Index + mGtin.Length);
        if (rest.StartsWith("21"))
        {
            var body = rest.Substring(2);
            int gsPos = body.IndexOf(GS);
            string serial;
            if (gsPos >= 0) serial = body.Substring(0, gsPos);
            else
            {
                // без GS: серийник максимум 20 символов, далее криптохвост 91/92/93
                var mNext = Regex.Match(body, @"(9[123])");
                serial = mNext.Success && mNext.Index <= 20 ? body.Substring(0, mNext.Index) : body;
                if (serial.Length > 20) serial = serial.Substring(0, 20);
            }
            r.Serial = serial;
        }
        else
        {
            r.Error = "В коде отсутствует серийный номер (AI 21)";
            return r;
        }

        var sb = new StringBuilder();
        sb.Append("01").Append(r.Gtin).Append("21").Append(r.Serial);
        r.Cis = sb.ToString();
        return r;
    }
}
