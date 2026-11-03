using System.IO.Ports;
using System.Text;
using MarkCheck.Data;
using MarkCheck.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace MarkCheck.Services;

/// <summary>
/// Фоновая служба чтения сканера через COM-порт.
/// Автоматически переподключается при отключении сканера,
/// собирает строку до CR/LF и передаёт её в обработчик.
/// </summary>
public class ScannerService : BackgroundService
{
    private readonly Db _db;
    private readonly IServiceProvider _sp;
    private readonly IHubContext<ScanHub> _hub;
    private readonly ILogger<ScannerService> _log;
    private readonly StringBuilder _buf = new();
    private SerialPort _port;
    private string _currentPortName;

    public string State { get; private set; } = "отключен";

    public ScannerService(Db db, IServiceProvider sp, IHubContext<ScanHub> hub, ILogger<ScannerService> log)
    { _db = db; _sp = sp; _hub = hub; _log = log; }

    public static string[] AvailablePorts()
    {
        try { return SerialPort.GetPortNames().Distinct().OrderBy(x => x).ToArray(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Принудительное переподключение (после смены настроек).</summary>
    public void Reconnect() => ClosePort();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var portName = _db.GetSetting("com_port");
                if (string.IsNullOrWhiteSpace(portName))
                {
                    await SetState("не настроен: укажите COM-порт в настройках", ct);
                    await Task.Delay(3000, ct);
                    continue;
                }

                if (_port == null || !_port.IsOpen || _currentPortName != portName)
                {
                    ClosePort();
                    _currentPortName = portName;
                    _port = new SerialPort(portName)
                    {
                        BaudRate = GetInt("com_baud", 9600),
                        DataBits = GetInt("com_databits", 8),
                        Parity = Enum.TryParse<Parity>(_db.GetSetting("com_parity", "None"), true, out var p) ? p : Parity.None,
                        StopBits = Enum.TryParse<StopBits>(_db.GetSetting("com_stopbits", "One"), true, out var sb) ? sb : StopBits.One,
                        Handshake = Handshake.None,
                        DtrEnable = true,
                        RtsEnable = true,
                        ReadTimeout = 500,
                        NewLine = "\r",
                        Encoding = Encoding.GetEncoding(28591) // Latin-1: коды маркировки ASCII-совместимы
                    };
                    _port.Open();
                    _buf.Clear();
                    await SetState($"подключён к {portName}", ct);
                    _log.LogInformation("Сканер подключён: {port}", portName);
                }

                // чтение данных
                while (!ct.IsCancellationRequested && _port.IsOpen)
                {
                    string chunk = null;
                    try { chunk = _port.ReadExisting(); }
                    catch (TimeoutException) { }
                    if (!string.IsNullOrEmpty(chunk)) await HandleChunk(chunk, ct);
                    else await Task.Delay(20, ct);

                    var cfg = _db.GetSetting("com_port");
                    if (cfg != _currentPortName) break; // порт сменили в настройках
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning("Сканер: {msg}", ex.Message);
                await SetState("ошибка порта: " + ex.Message, ct);
                ClosePort();
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }
        ClosePort();
    }

    private async Task HandleChunk(string chunk, CancellationToken ct)
    {
        foreach (var ch in chunk)
        {
            if (ch == '\r' || ch == '\n')
            {
                var code = _buf.ToString();
                _buf.Clear();
                if (!string.IsNullOrWhiteSpace(code)) await Dispatch(code, ct);
            }
            else _buf.Append(ch);
        }
        // некоторые сканеры не отправляют терминатор — отдаём по таймауту тишины
        if (_buf.Length > 0)
        {
            var snapshot = _buf.ToString();
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                if (_buf.Length == snapshot.Length && _buf.ToString() == snapshot)
                {
                    _buf.Clear();
                    await Dispatch(snapshot, CancellationToken.None);
                }
            });
        }
    }

    private async Task Dispatch(string code, CancellationToken ct)
    {
        _log.LogInformation("Скан: {code}", code);
        using var scope = _sp.CreateScope();
        var proc = scope.ServiceProvider.GetRequiredService<ScanProcessor>();
        var res = await proc.ProcessAsync(code.Trim(), ct);
        await _hub.Clients.All.SendAsync("scanResult", res, ct);
    }

    private int GetInt(string key, int def) => int.TryParse(_db.GetSetting(key), out var v) ? v : def;

    private async Task SetState(string s, CancellationToken ct)
    {
        if (State == s) return;
        State = s;
        try { await _hub.Clients.All.SendAsync("scannerState", s, ct); } catch { }
    }

    private void ClosePort()
    {
        try { _port?.Close(); _port?.Dispose(); } catch { }
        _port = null;
    }
}
