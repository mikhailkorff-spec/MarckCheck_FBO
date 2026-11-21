using System.Text.Json;
using MarkCheck.Data;

namespace MarkCheck.Services;

/// <summary>
/// Отбивка в Битрикс24 через входящий вебхук (метод im.message.add), по образцу WbPackStation.
/// Настройки: bitrix_webhook — URL вебхука с токеном (хранится зашифрованным),
/// bitrix_rules — строки вида «БУСТ ЛАЙФ = chat12345»: если путь печатной формы содержит папку
/// с таким именем, сообщение уходит в указанный диалог (чат или пользователь).
/// Ошибки отправки не прерывают завершение сборки — возвращаются текстом для журнала.
/// </summary>
public class BitrixNotifier
{
    private readonly Db _db;
    private readonly SecretProtector _secret;
    private readonly IHttpClientFactory _http;

    public BitrixNotifier(Db db, SecretProtector secret, IHttpClientFactory http)
    { _db = db; _secret = secret; _http = http; }

    /// <summary>Правила маршрутизации «папка = DIALOG_ID».</summary>
    public List<(string Folder, string DialogId)> Rules()
    {
        var text = _db.GetSetting("bitrix_rules", "") ?? "";
        return text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#") && l.Contains('='))
            .Select(l => (l[..l.IndexOf('=')].Trim(), l[(l.IndexOf('=') + 1)..].Trim()))
            .Where(r => r.Item1.Length > 0 && r.Item2.Length > 0)
            .ToList();
    }

    /// <summary>Подбирает диалог по папкам относительного пути (без учёта регистра). Null — отбивка не нужна.</summary>
    public string ResolveDialog(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var dirs = (Path.GetDirectoryName(relativePath) ?? "")
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var (folder, dialog) in Rules())
            if (dirs.Any(d => d.Contains(folder, StringComparison.OrdinalIgnoreCase)))
                return dialog;
        return null;
    }

    public static string BuildFboMessage(string instruction, int units, int boxes) =>
        $"ФБО\n{instruction}\nСобран.\nКоличество единиц: {units}\nКоличество коробов: {boxes}";

    /// <summary>Отправляет сообщение. Возвращает null при успехе или текст ошибки.</summary>
    public async Task<string> SendAsync(string dialogId, string message)
    {
        var enc = _db.GetSetting("bitrix_webhook");
        if (string.IsNullOrWhiteSpace(enc)) return "не задан вебхук Битрикс24";
        string url;
        try { url = _secret.Decrypt(enc); }
        catch { return "не удалось расшифровать вебхук (сменился файл data/secret.key?) — введите его заново"; }
        if (!url.EndsWith('/')) url += "/";
        try
        {
            var client = _http.CreateClient(nameof(BitrixNotifier));
            client.Timeout = TimeSpan.FromSeconds(15);
            // Обычный JSON с Content-Length (без chunked) — так надёжнее через прокси.
            var json = JsonSerializer.Serialize(new { DIALOG_ID = dialogId, MESSAGE = message });
            var resp = await client.PostAsync(url + "im.message.add", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return $"HTTP {(int)resp.StatusCode}: {Trim(body)}";
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString() + " " + (doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : "");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string Trim(string s) => s == null ? "" : s.Length > 200 ? s[..200] : s;
}
