using System.Text;
using System.Text.Json;
using System.Linq;
using MarkCheck.Data;

namespace MarkCheck.Services;

public class CzCheckResult
{
    public bool Ok { get; set; }
    public bool Skipped { get; set; }
    public string Message { get; set; }
    public string Raw { get; set; }
    public string OwnerInn { get; set; }
    public string Status { get; set; }
}

/// <summary>
/// Клиент True API «Честного ЗНАКа» — разрешительный режим (проверка перед продажей).
/// MVP: токен ККТ выдаётся вручную в разделе «Администрирование» (генерируется в ЛК: Профиль →
/// «Токен для контрольно-кассовой техники»). С 2024 г. метод codes/check вызывается не на
/// markirovka.crpt.ru, а на CDN-площадках (cdn.crpt.ru как единая точка входа), авторизация — X-API-KEY.
/// Есть офлайн-тестовый режим (cz_offline_mode=1): имитирует ответ ЧЗ без реального запроса.
/// </summary>
public class TrueApiClient
{
    private readonly IHttpClientFactory _http;
    private readonly Db _db;
    private readonly ILogger<TrueApiClient> _log;
    private static readonly Random _rnd = new();

    public TrueApiClient(IHttpClientFactory http, Db db, ILogger<TrueApiClient> log)
    { _http = http; _db = db; _log = log; }

    public async Task<CzCheckResult> CheckAsync(string cis, CancellationToken ct = default)
    {
        var enabled = _db.GetSetting("cz_enabled", "1") == "1";
        var offlineMode = _db.GetSetting("cz_offline_mode", "0") == "1";

        if (!enabled)
            return new CzCheckResult { Ok = true, Skipped = true, Message = "Проверка ЧЗ отключена" };

        if (offlineMode)
            return SimulateOfflineCheck(cis);

        var token = _db.GetSetting("cz_token");
        var baseUrl = _db.GetSetting("cz_base_url", "https://cdn.crpt.ru");
        var ownInn = _db.GetSetting("company_inn");

        if (string.IsNullOrWhiteSpace(token))
            return new CzCheckResult { Ok = true, Skipped = true, Message = "Проверка ЧЗ отключена (нет токена)" };

        try
        {
            var client = _http.CreateClient("cz");
            client.Timeout = TimeSpan.FromSeconds(int.TryParse(_db.GetSetting("cz_timeout", "10"), out var t) ? t : 10);

            // cdn.crpt.ru — точка входа для обнаружения площадок, а не рабочий адрес codes/check.
            // Если пользователь задал конкретную площадку (не cdn.crpt.ru) — используем её как есть.
            var host = baseUrl.TrimEnd('/');
            var isBalancer = host.Equals("https://cdn.crpt.ru", StringComparison.OrdinalIgnoreCase)
                          || host.Equals("http://cdn.crpt.ru", StringComparison.OrdinalIgnoreCase);
            if (isBalancer)
                host = await ResolveCdnHostAsync(client, token, ct) ?? host;

            var url = $"{host}/api/v4/true-api/codes/check";
            var body = JsonSerializer.Serialize(new { codes = new[] { cis } });
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("X-API-KEY", token);

            using var resp = await client.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            // Площадка недоступна/устарела — сбрасываем кеш, пробуем один раз заново с обнаружением.
            if (isBalancer && (int)resp.StatusCode == 404)
            {
                _db.SetSetting("cz_resolved_host", "");
                var freshHost = await ResolveCdnHostAsync(client, token, ct);
                if (!string.IsNullOrEmpty(freshHost) && freshHost != host)
                {
                    var url2 = $"{freshHost}/api/v4/true-api/codes/check";
                    using var req2 = new HttpRequestMessage(HttpMethod.Post, url2)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    };
                    req2.Headers.TryAddWithoutValidation("X-API-KEY", token);
                    using var resp2 = await client.SendAsync(req2, ct);
                    text = await resp2.Content.ReadAsStringAsync(ct);
                    if (!resp2.IsSuccessStatusCode)
                        return new CzCheckResult { Ok = false, Raw = text, Message = $"ЧЗ HTTP {(int)resp2.StatusCode}: {Short(text)}" };
                    return ParseCheckResponse(text, ownInn);
                }
            }

            if (!resp.IsSuccessStatusCode)
                return new CzCheckResult { Ok = false, Raw = text, Message = $"ЧЗ HTTP {(int)resp.StatusCode}: {Short(text)}" };

            return ParseCheckResponse(text, ownInn);
        }
        catch (TaskCanceledException)
        {
            return new CzCheckResult { Ok = false, Message = "ЧЗ: таймаут запроса" };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ошибка обращения к ЧЗ");
            return new CzCheckResult { Ok = false, Message = "ЧЗ: ошибка связи — " + ex.Message };
        }
    }

    /// <summary>Разбирает ответ codes/check в результат проверки.</summary>
    private static CzCheckResult ParseCheckResponse(string text, string ownInn)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("codes", out var codes) || codes.GetArrayLength() == 0)
            return new CzCheckResult { Ok = false, Raw = text, Message = "ЧЗ: пустой ответ по коду" };

        var c = codes[0];
        bool valid = GetBool(c, "valid");
        bool found = GetBool(c, "found", true);
        bool realizable = GetBool(c, "realizable", true);
        bool utilised = GetBool(c, "utilised", true);
        string status = GetStr(c, "status");
        string owner = GetStr(c, "ownerInn") ?? GetStr(c, "ownerInn ");
        string errCode = GetStr(c, "errorCode");

        var problems = new List<string>();
        if (!found) problems.Add("код не найден в ЧЗ");
        if (!valid) problems.Add("код невалиден (проверка КМ не пройдена)");
        if (!utilised) problems.Add("код не введён в оборот");
        if (!realizable) problems.Add("код не может быть реализован");
        if (!string.IsNullOrEmpty(status) &&
            !status.Equals("INTRODUCED", StringComparison.OrdinalIgnoreCase) &&
            !status.Equals("APPLIED", StringComparison.OrdinalIgnoreCase))
            problems.Add($"статус {status}");
        if (!string.IsNullOrWhiteSpace(ownInn) && !string.IsNullOrWhiteSpace(owner) &&
            !owner.Trim().Equals(ownInn.Trim(), StringComparison.Ordinal))
            problems.Add($"код принадлежит ИНН {owner}, ожидался {ownInn}");
        if (!string.IsNullOrEmpty(errCode) && errCode != "0") problems.Add($"errorCode={errCode}");

        return new CzCheckResult
        {
            Ok = problems.Count == 0,
            Status = status,
            OwnerInn = owner,
            Raw = text,
            Message = problems.Count == 0 ? $"ЧЗ: OK ({status})" : "ЧЗ: " + string.Join("; ", problems)
        };
    }

    /// <summary>
    /// Обнаружение актуальной CDN-площадки через cdn/info (с кешированием в Settings на 2 часа).
    /// При наличии нескольких площадок берётся самая быстрая по cdn/health/check.
    /// </summary>
    private async Task<string> ResolveCdnHostAsync(HttpClient client, string token, CancellationToken ct)
    {
        var cached = _db.GetSetting("cz_resolved_host");
        var cachedAtStr = _db.GetSetting("cz_resolved_at");
        if (!string.IsNullOrEmpty(cached) && DateTime.TryParse(cachedAtStr, out var cachedAt)
            && DateTime.UtcNow - cachedAt < TimeSpan.FromHours(2))
            return cached;

        try
        {
            using var infoReq = new HttpRequestMessage(HttpMethod.Get, "https://cdn.crpt.ru/api/v4/true-api/cdn/info");
            infoReq.Headers.TryAddWithoutValidation("X-API-KEY", token);
            using var infoResp = await client.SendAsync(infoReq, ct);
            if (!infoResp.IsSuccessStatusCode) return cached;

            var infoText = await infoResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(infoText);
            if (!doc.RootElement.TryGetProperty("hosts", out var hostsEl) || hostsEl.GetArrayLength() == 0)
                return cached;

            var hosts = hostsEl.EnumerateArray()
                .Select(h => GetStr(h, "host"))
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .ToList();
            if (hosts.Count == 0) return cached;

            string best = hosts[0];
            long bestMs = long.MaxValue;
            foreach (var h in hosts)
            {
                try
                {
                    using var hReq = new HttpRequestMessage(HttpMethod.Get, $"{h.TrimEnd('/')}/api/v4/true-api/cdn/health/check");
                    hReq.Headers.TryAddWithoutValidation("X-API-KEY", token);
                    using var hResp = await client.SendAsync(hReq, ct);
                    if (!hResp.IsSuccessStatusCode) continue;
                    var hText = await hResp.Content.ReadAsStringAsync(ct);
                    using var hDoc = JsonDocument.Parse(hText);
                    var ms = hDoc.RootElement.TryGetProperty("avgTimeMs", out var msEl) && msEl.TryGetInt64(out var v) ? v : long.MaxValue;
                    if (ms < bestMs) { bestMs = ms; best = h; }
                }
                catch { /* площадка недоступна — пробуем следующую */ }
            }

            _db.SetSetting("cz_resolved_host", best);
            _db.SetSetting("cz_resolved_at", DateTime.UtcNow.ToString("O"));
            _log.LogInformation("Выбрана CDN-площадка ЧЗ: {Host}", best);
            return best;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось обнаружить CDN-площадку ЧЗ");
            return cached;
        }
    }

    /// <summary>Имитация ответа ЧЗ для офлайн-теста: случайно успех/отказ без обращения в сеть.</summary>
    private static CzCheckResult SimulateOfflineCheck(string cis)
    {
        bool success = _rnd.NextDouble() < 0.85; // 85% — успех
        if (success)
        {
            return new CzCheckResult
            {
                Ok = true,
                Status = "INTRODUCED",
                Message = "ЧЗ (офлайн-тест): OK (имитация)",
                Raw = "{\"simulated\":true,\"result\":\"ok\"}"
            };
        }

        var reasons = new[]
        {
            "код не найден в ЧЗ (имитация)",
            "код уже выведен из оборота (имитация)",
            "истёк срок годности (имитация)",
            "код заблокирован ОГВ (имитация)",
            "код невалиден — криптопроверка не пройдена (имитация)"
        };
        var reason = reasons[_rnd.Next(reasons.Length)];
        return new CzCheckResult
        {
            Ok = false,
            Status = "SIMULATED_ERROR",
            Message = "ЧЗ (офлайн-тест): " + reason,
            Raw = "{\"simulated\":true,\"result\":\"fail\",\"reason\":\"" + reason + "\"}"
        };
    }

    private static bool GetBool(JsonElement e, string name, bool def = false) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;

    private static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Short(string s) => s == null ? "" : (s.Length > 200 ? s.Substring(0, 200) : s);

}
