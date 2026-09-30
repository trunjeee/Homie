using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace Homie.Services;

/// <summary>
/// Сообщения от Алисы: навык «Хоуми» в Яндекс Облаке подписывает фразу «Ключом связи» и кладёт её
/// в канал ntfy.sh, а Homie слушает канал и принимает только сообщения с верной подписью.
/// Код навыка — в папке alice-skill.
/// </summary>
public sealed class AliceRelay : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2); // старые (пока ПК спал) не зачитываем
    private readonly DispatcherQueue _ui;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? _cts;
    private string? _key;

    /// <summary>Пришло проверенное сообщение (в UI-потоке).</summary>
    public event Action<string>? MessageReceived;
    /// <summary>Подключились к каналу / потеряли связь.</summary>
    public event Action<bool>? ConnectedChanged;
    public bool IsConnected { get; private set; }

    public AliceRelay(DispatcherQueue ui) => _ui = ui;

    /// <summary>Новый случайный ключ связи (его же вписываешь в навык как HOMIE_KEY).</summary>
    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>Имя канала — из ключа, так же, как в навыке.</summary>
    public static string TopicFor(string key) =>
        "homie-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("topic:" + key)))[..32].ToLowerInvariant();

    public void Start(string key)
    {
        Stop();
        _key = key;
        _cts = new CancellationTokenSource();
        _ = ListenAsync(key, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        SetConnected(false);
    }

    /// <summary>Проверка из настроек: отправить себе подписанное сообщение тем же путём, что и навык.</summary>
    public async Task SendTestAsync(string text)
    {
        var key = _key ?? throw new InvalidOperationException("Канал не запущен");
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string body = JsonSerializer.Serialize(new Dictionary<string, object> { ["text"] = text, ["ts"] = ts, ["sig"] = Sign(key, ts, text) });
        using var content = new StringContent(body, Encoding.UTF8);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        (await _http.PostAsync($"https://ntfy.sh/{TopicFor(key)}", content, cts.Token)).EnsureSuccessStatusCode();
    }

    /// <summary>Слушаем канал потоком (строки JSON); оборвалось — переподключаемся с паузой.</summary>
    private async Task ListenAsync(string key, CancellationToken ct)
    {
        string topic = TopicFor(key);
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var response = await _http.GetAsync($"https://ntfy.sh/{topic}/json", HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                SetConnected(true);
                delay = TimeSpan.FromSeconds(2);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                while (await reader.ReadLineAsync(ct) is { } line)
                    Handle(key, line);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // нет интернета или канал оборвался — ниже переподключимся
            }
            SetConnected(false);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    private void Handle(string key, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("event").GetString() != "message") return; // open / keepalive
            using var payload = JsonDocument.Parse(root.GetProperty("message").GetString() ?? "");
            var p = payload.RootElement;
            string text = p.GetProperty("text").GetString() ?? "";
            long ts = p.GetProperty("ts").GetInt64();
            string sig = p.GetProperty("sig").GetString() ?? "";

            // Подпись не сошлась — это не наш навык; слишком старое — не зачитываем.
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(key, ts, text)), Encoding.ASCII.GetBytes(sig))) return;
            if (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts) > MaxAge) return;
            if (string.IsNullOrWhiteSpace(text)) return;
            _ui.TryEnqueue(() => MessageReceived?.Invoke(text.Trim()));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // чужое или битое сообщение — пропускаем
        }
    }

    private static string Sign(string key, long ts, string text) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{ts}\n{text}"))).ToLowerInvariant();

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        _ui.TryEnqueue(() => ConnectedChanged?.Invoke(value));
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
