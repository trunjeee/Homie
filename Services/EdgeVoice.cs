using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security;
using System.Text;

namespace Homie.Services;

/// <summary>
/// Голос «Светлана» (Microsoft, Natural) — тот же, что у «Прочесть вслух» в Edge. Бесплатно, нужен интернет.
/// Это неофициальный способ (как в проекте edge-tts): если Microsoft его поменяет, ответ покажется только текстом.
/// </summary>
public static class EdgeVoice
{
    public const string Svetlana = "ru-RU-SvetlanaNeural";
    public const string Dmitry = "ru-RU-DmitryNeural";

    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string ChromiumVersion = "143.0.3650.75";
    private const string Url = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";

    /// <summary>Синтез речи; вернёт mp3 (24 кГц).</summary>
    public static async Task<byte[]> SynthesizeAsync(string text, string voice = Svetlana, CancellationToken ct = default)
    {
        string major = ChromiumVersion.Split('.')[0];
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Cookie", $"muid={Convert.ToHexString(RandomNumberGenerator.GetBytes(16))};");

        var uri = new Uri($"{Url}?TrustedClientToken={TrustedClientToken}&ConnectionId={Guid.NewGuid():N}" +
                          $"&Sec-MS-GEC={SecMsGec()}&Sec-MS-GEC-Version=1-{ChromiumVersion}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await ws.ConnectAsync(uri, timeout.Token);

        string date = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            System.Globalization.CultureInfo.InvariantCulture);
        await SendText(ws,
            $"X-Timestamp:{date}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
            "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n", timeout.Token);

        string ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='ru-RU'>" +
                      $"<voice name='Microsoft Server Speech Text to Speech Voice (ru-RU, {voice.Replace("ru-RU-", "")})'>" +
                      $"<prosody pitch='+0Hz' rate='+0%' volume='+0%'>{SecurityElement.Escape(text)}</prosody></voice></speak>";
        await SendText(ws,
            $"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{date}Z\r\nPath:ssml\r\n\r\n{ssml}",
            timeout.Token);

        // Ответ: бинарные кадры «2 байта длины заголовка + заголовок + mp3», в конце текстовый Path:turn.end.
        using var audio = new MemoryStream();
        var buffer = new byte[16384];
        using var frame = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            frame.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, timeout.Token);
                frame.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) break;
            var data = frame.GetBuffer().AsSpan(0, (int)frame.Length);
            if (result.MessageType == WebSocketMessageType.Text)
            {
                if (Encoding.UTF8.GetString(data).Contains("Path:turn.end")) break;
                continue;
            }
            if (data.Length < 2) continue;
            int headerLength = (data[0] << 8) | data[1];
            if (2 + headerLength > data.Length) continue;
            if (!Encoding.ASCII.GetString(data.Slice(2, headerLength)).Contains("Path:audio")) continue;
            audio.Write(data[(2 + headerLength)..]);
        }
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        if (audio.Length == 0) throw new IOException("Голос не вернул звук");
        return audio.ToArray();
    }

    private static Task SendText(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);

    /// <summary>Токен Edge: SHA256 от времени (с шагом 5 минут, в тиках Windows) и публичного ключа клиента.</summary>
    private static string SecMsGec()
    {
        long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11644473600L;
        seconds -= seconds % 300;
        string value = (seconds * 10_000_000L).ToString(System.Globalization.CultureInfo.InvariantCulture) + TrustedClientToken;
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
    }
}
