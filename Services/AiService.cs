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

    /// <summary>Спросить; при ошибке основной модели — запасную. Бросит AiException с понятным текстом.</summary>
    public async Task<string> AskAsync(string question, AppSettings settings, CancellationToken ct = default)
    {
        var key = SettingsStore.LoadAiKey() ?? throw new AiException("Добавь ключ OpenRouter в настройках");
        var models = new[] { settings.AiModel, settings.AiFallbackModel }
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct().ToList();
        if (models.Count == 0) throw new AiException("Не выбрана модель");

        AiException? last = null;
        foreach (var model in models)
        {
            try
            {
                return await AskModelAsync(key, model, question, ct);
            }
            catch (AiException ex) when (!ex.Fatal)
            {
                last = ex; // модель перегружена или недоступна — пробуем запасную
            }
        }
        throw last!;
    }

    private async Task<string> AskModelAsync(string key, string model, string question, CancellationToken ct)
    {
        // У некоторых моделей «размышления» обязательны — тогда повторяем без отключения.
        foreach (bool disableReasoning in new[] { true, false })
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 400,
                ["temperature"] = 0.6,
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
                response = await _http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new AiException("Нет связи с нейросетью — проверь интернет");
            }

            using (response)
            {
                string json = await response.Content.ReadAsStringAsync(ct);
                switch (response.StatusCode)
                {
                    case HttpStatusCode.Unauthorized:
                        throw new AiException("Ключ OpenRouter не подошёл — проверь его в настройках", fatal: true);
                    case HttpStatusCode.PaymentRequired:
                        throw new AiException("OpenRouter просит пополнить баланс — выбери модель с «:free»");
                    case HttpStatusCode.TooManyRequests:
                        throw new AiException("Лимит бесплатных вопросов закончился — попробуй позже");
                    case HttpStatusCode.BadRequest when disableReasoning:
                        continue;
                }
                if (!response.IsSuccessStatusCode) throw new AiException($"Нейросеть не ответила ({(int)response.StatusCode})");

                try
                {
                    var answer = JsonNode.Parse(json)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(answer)) throw new AiException("Нейросеть вернула пустой ответ");
                    return Clean(answer);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    throw new AiException("Нейросеть ответила непонятно");
                }
            }
        }
        throw new AiException("Модель не приняла запрос");
    }

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
