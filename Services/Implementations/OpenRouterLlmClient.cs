using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TelegramPhishDetector.Services.Implementations;

/// <summary>
/// Клиент LLM через <see href="https://openrouter.ai/docs">OpenRouter</see>:
/// совместимый с OpenAI <c>chat/completions</c> эндпоинт.
/// </summary>
public sealed class OpenRouterLlmClient : ILlmClient
{
    /// <seealso href="https://openrouter.ai/docs/quickstart"/>
    private const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly ILogger<OpenRouterLlmClient> _logger;

    public OpenRouterLlmClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<OpenRouterLlmClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var apiKey = configuration["OpenRouterApiKey"]
                     ?? throw new InvalidOperationException("Не задан OpenRouterApiKey в конфигурации.");

        _model = configuration["OpenRouterModel"]
                 ?? throw new InvalidOperationException("Не задан OpenRouterModel в конфигурации (slug, например openrouter/auto).");

        var baseUrlRaw = configuration["OpenRouterBaseUrl"]?.Trim();
        var root = string.IsNullOrWhiteSpace(baseUrlRaw)
            ? DefaultBaseUrl
            : baseUrlRaw.TrimEnd('/');

        _httpClient.BaseAddress = new Uri($"{root}/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Опциональная атрибуция приложения для рейтингов на openrouter.ai
        var referer = configuration["OpenRouterHttpReferer"]?.Trim();
        if (!string.IsNullOrEmpty(referer))
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", referer);

        var title = configuration["OpenRouterAppTitle"]?.Trim();
        if (!string.IsNullOrEmpty(title))
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-OpenRouter-Title", title);
    }

    public async Task<string> GetCompletionAsync(string prompt, CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model = _model,
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = 500,
            temperature = 0.1,
            response_format = new { type = "json_object" },
        };

        _logger.LogInformation(
            "[OpenRouter] Запрос chat/completions: model={Model}, длина промпта={PromptLen}",
            _model,
            prompt.Length);

        var sw = Stopwatch.StartNew();
        using var response = await _httpClient
            .PostAsJsonAsync("chat/completions", requestBody, cancellationToken)
            .ConfigureAwait(false);

        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            var preview = body.Length > 800 ? body[..800] + "…" : body;
            _logger.LogWarning(
                "[OpenRouter] Ошибка HTTP {Status} за {ElapsedMs} мс. Retry-After={Retry}. Тело: {Body}",
                (int)response.StatusCode,
                sw.ElapsedMilliseconds,
                response.Headers.RetryAfter?.Delta?.TotalSeconds,
                preview);
            response.EnsureSuccessStatusCode();
        }

        var json = await response.Content
            .ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!json.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            _logger.LogWarning("[OpenRouter] Ответ без choices или пустой массив, возвращаю {{}}");
            return "{}";
        }

        var msg = choices[0].GetProperty("message");
        var answer = msg.TryGetProperty("content", out var content)
            ? content.GetString() ?? "{}"
            : "{}";

        _logger.LogInformation(
            "[OpenRouter] Успешно за {ElapsedMs} мс, длина content={AnswerLen}, choices={ChoiceCount}",
            sw.ElapsedMilliseconds,
            answer.Length,
            choices.GetArrayLength());

        return answer;
    }
}
