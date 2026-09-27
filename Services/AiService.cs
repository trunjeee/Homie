using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Homie.Services;

/// <summary>
/// Ответы на вопросы через OpenRouter (бесплатные модели «:free»). Ключ — у пользователя в настройках.
/// Сюда попадает только то, что Homie не понял как команду.
/// </summary>
public sealed partial class AiService : IDisposable
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };

    public static bool HasKey => SettingsStore.LoadAiKey() is not null;

    /// <summary>
    /// Спросить; при ошибке основной модели — запасную. Ответ приходит потоком: onText получает
    /// весь накопленный на данный момент текст (для озвучки по предложениям). Бросит AiException.
    /// </summary>
    public async Task<string> AskAsync(string question, AppSettings settings, Action<string>? onText = null, CancellationToken ct = default)
    {
        var key = SettingsStore.LoadAiKey() ?? throw new AiException("Добавь ключ OpenRouter в настройках");
        // Запасных можно несколько через запятую — пробуем по очереди.
        var models = new[] { settings.AiModel }.Concat(settings.AiFallbackModel.Split(',', ';'))
            .Select(m => m.Trim()).Where(m => m.Length > 0).Distinct().ToList();
        if (models.Count == 0) throw new AiException("Не выбрана модель");

        AiException? last = null;
        foreach (var model in models)
        {
            try
            {
                return await AskModelAsync(key, model, question, onText, ct);
            }
            catch (AiException ex) when (!ex.Fatal)
            {
                last = ex; // модель перегружена или недоступна — пробуем запасную
            }
        }
        throw last!;
    }

    private async Task<string> AskModelAsync(string key, string model, string question, Action<string>? onText, CancellationToken ct)
    {
        // У некоторых моделей «размышления» обязательны — тогда повторяем без отключения.
        foreach (bool disableReasoning in new[] { true, false })
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 400,
                ["temperature"] = 0.6,
                ["stream"] = true,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = SystemPrompt() },
                    new JsonObject { ["role"] = "user", ["content"] = question }),
            };
            if (disableReasoning) body["reasoning"] = new JsonObject { ["effort"] = "none" };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Add("HTTP-Referer", "https://github.com/trunjeee/Homie");
            request.Headers.Add("X-Title", "Homie");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new AiException("Нет связи с нейросетью — проверь интернет");
            }

            using (response)
            {
                if (response.IsSuccessStatusCode) return await ReadStreamAsync(response, model, onText, ct);

                string json = await response.Content.ReadAsStringAsync(ct);
                string server = ErrorText(json);
                switch (response.StatusCode)
                {
                    case HttpStatusCode.Unauthorized:
                        throw new AiException("Ключ OpenRouter не подошёл — проверь его в настройках", fatal: true);
                    case HttpStatusCode.PaymentRequired:
                        throw new AiException($"{model}: OpenRouter просит пополнить баланс — выбери модель с «:free»");
                    // Настоящий дневной лимит аккаунта — другие модели тоже не помогут.
                    case HttpStatusCode.TooManyRequests when server.Contains("per-day", StringComparison.OrdinalIgnoreCase):
                        throw new AiException("Лимит бесплатных вопросов на сегодня закончился", fatal: true);
                    // Иначе 429 — это перегрузка у поставщика бесплатной модели: пробуем следующую.
                    case HttpStatusCode.TooManyRequests:
                        throw new AiException($"{ShortName(model)} сейчас перегружена, попробуй чуть позже");
                    case HttpStatusCode.NotFound when server.Contains("data policy", StringComparison.OrdinalIgnoreCase):
                        throw new AiException("Разреши бесплатные модели в настройках приватности OpenRouter (openrouter.ai/settings/privacy)", fatal: true);
                    case HttpStatusCode.BadRequest when disableReasoning:
                        continue;
                }
                throw new AiException($"{ShortName(model)}: ошибка {(int)response.StatusCode}{(server.Length > 0 ? " — " + server : "")}");
            }
        }
        throw new AiException("Модель не приняла запрос");
    }

    /// <summary>Поток ответа (SSE): строки «data: {…}» с кусочками текста, в конце «data: [DONE]».</summary>
    private static async Task<string> ReadStreamAsync(HttpResponseMessage response, string model, Action<string>? onText, CancellationToken ct)
    {
        var text = new StringBuilder();
        try
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:")) continue; // пустые строки и «: OPENROUTER PROCESSING»
                string data = line[5..].Trim();
                if (data == "[DONE]") break;

                JsonNode? chunk;
                try { chunk = JsonNode.Parse(data); }
                catch (JsonException) { continue; }

                if (chunk?["error"] is { } error)
                {
                    // Ошибка посреди ответа: если что-то уже пришло — отдаём это, иначе пробуем другую модель.
                    if (text.Length > 0) break;
                    throw new AiException($"{ShortName(model)}: {error["message"]?.ToString() ?? "ошибка"}");
                }
                string? delta = chunk?["choices"]?[0]?["delta"]?["content"]?.ToString();
                if (string.IsNullOrEmpty(delta)) continue;
                text.Append(delta);
                onText?.Invoke(text.ToString());
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException && text.Length == 0)
        {
            throw new AiException("Нет связи с нейросетью — проверь интернет");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        {
            // Связь оборвалась, но часть ответа есть — её и используем.
        }

        string answer = Clean(text.ToString());
        if (answer.Length == 0) throw new AiException($"{ShortName(model)} вернула пустой ответ");
        return answer;
    }

    /// <summary>
    /// Разбить накопленный ответ на готовые предложения для озвучки. Вернёт новые законченные
    /// предложения начиная с позиции from и сдвинет её. Короткие куски склеиваются, чтобы речь не рвалась.
    /// </summary>
    public static List<string> TakeSentences(string text, ref int from, bool final)
    {
        var result = new List<string>();
        int start = from;
        for (int i = from; i < text.Length; i++)
        {
            bool end = text[i] is '.' or '!' or '?' or '…' or '\n';
            bool boundary = end && (i + 1 == text.Length ? final : char.IsWhiteSpace(text[i + 1]));
            if (boundary && text[i] == '.' && i >= 2 && text[i - 2] == '.') continue; // «т.е.», «т.к.», «т.д.»
            if (!boundary || i + 1 - start < 25) continue; // совсем короткие куски — ждём продолжения
            string sentence = Clean(text[start..(i + 1)]);
            if (sentence.Length > 0) result.Add(sentence);
            start = i + 1;
        }
        if (final && start < text.Length)
        {
            string rest = Clean(text[start..]);
            if (rest.Length > 0) result.Add(rest);
            start = text.Length;
        }
        from = start;
        return result;
    }

    /// <summary>Текст ошибки от OpenRouter/поставщика (коротко).</summary>
    private static string ErrorText(string json)
    {
        try
        {
            var error = JsonNode.Parse(json)?["error"];
            string message = error?["message"]?.GetValue<string>() ?? "";
            string raw = error?["metadata"]?["raw"]?.ToString() ?? "";
            string text = raw.Length > 0 && !message.Contains(raw) ? $"{message} ({raw})" : message;
            return text.Length > 200 ? text[..200] + "…" : text;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return "";
        }
    }

    private static string ShortName(string model) => model.Split('/').Last().Replace(":free", "");

    private static string SystemPrompt() =>
        "Ты — Хоуми, голосовой помощник на компьютере пользователя (умный дом, игры, повседневные вопросы). " +
        "Отвечай по-русски, дружелюбно и коротко: одно-два предложения. Ответ будет озвучен голосом, " +
        "поэтому без списков, markdown, эмодзи и ссылок; единицы измерения пиши словами. " +
        $"Сейчас {DateTime.Now:dd.MM.yyyy HH:mm}.";

    /// <summary>Убрать разметку, которую модели всё равно иногда добавляют.</summary>
    private static string Clean(string text)
    {
        text = Markdown().Replace(text, "");
        text = Spaces().Replace(text, " ");
        return text.Trim();
    }

    [GeneratedRegex(@"[*_#`>]+|\[(?:[^\]]*)\]\([^)]*\)")]
    private static partial Regex Markdown();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public void Dispose() => _http.Dispose();
}

public sealed class AiException(string message, bool fatal = false) : Exception(message)
{
    /// <summary>Нет смысла пробовать другую модель (например, неверный ключ).</summary>
    public bool Fatal { get; } = fatal;
}
