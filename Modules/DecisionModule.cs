using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using TelegramPhishDetector.Models;
using TelegramPhishDetector.Services;

namespace TelegramPhishDetector.Modules;

public class DecisionModule
{
    private readonly ILlmClient _llmClient;
    private readonly PromptEngineeringModule _promptBuilder;
    private readonly ILogger<DecisionModule> _logger;

    public DecisionModule(
        ILlmClient llmClient,
        PromptEngineeringModule promptBuilder,
        ILogger<DecisionModule> logger)
    {
        _llmClient = llmClient;
        _promptBuilder = promptBuilder;
        _logger = logger;
    }

    public async Task<PhishingDecision> DecideAsync(Message msg, CancellationToken ct)
    {
        var swDecision = Stopwatch.StartNew();
        _logger.LogInformation("[Решение] Старт анализа сообщения {MessageId}", msg.Id);

        var sw = Stopwatch.StartNew();
        string prompt;
        try
        {
            _logger.LogInformation("[Промпт] Начато построение промпта для сообщения {MessageId}", msg.Id);
            prompt = await _promptBuilder.BuildPromptAsync(msg, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Промпт] Ошибка построения промпта для сообщения {MessageId}", msg.Id);
            return new PhishingDecision
            {
                Verdict = "error",
                Confidence = 0,
                Reasons = new List<string> { $"Промпт не собран: {ex.GetType().Name}" },
            };
        }

        _logger.LogInformation(
            "[Промпт] Готов за {ElapsedMs} мс, длина символов: {PromptLength}",
            sw.ElapsedMilliseconds,
            prompt.Length);

        sw.Restart();
        string response;
        try
        {
            _logger.LogInformation("[LLM] Начат запрос к модели для сообщения {MessageId}", msg.Id);
            response = await _llmClient.GetCompletionAsync(prompt, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LLM] Запрос к модели завершился с ошибкой для сообщения {MessageId}", msg.Id);
            return new PhishingDecision
            {
                Verdict = "error",
                Confidence = 0,
                Reasons = new List<string> { $"Ошибка LLM: {ex.Message}" },
            };
        }

        _logger.LogInformation(
            "[LLM] Ответ получен за {ElapsedMs} мс, длина ответа: {Len} символов",
            sw.ElapsedMilliseconds,
            response.Length);

        try
        {
            _logger.LogInformation("[Разбор JSON] Начат разбор ответа LLM для сообщения {MessageId}", msg.Id);
            var decision = JsonSerializer.Deserialize<PhishingDecision>(
                response,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (decision == null)
            {
                _logger.LogWarning("[Разбор JSON] Ответ LLM после десериализации равен null");
                return new PhishingDecision { Verdict = "error", Confidence = 0 };
            }

            swDecision.Stop();
            _logger.LogInformation(
                "[Решение] Анализ завершен за {ElapsedMs} мс: verdict={Verdict}, confidence={Confidence:P0}, reasons={ReasonsCount}, suspiciousPhrases={PhrasesCount}",
                swDecision.ElapsedMilliseconds,
                decision.Verdict,
                decision.Confidence,
                decision.Reasons.Count,
                decision.SuspiciousPhrases.Count);
            return decision;
        }
        catch (Exception ex)
        {
            var preview = response.Length == 0
                ? "—пустой ответ—"
                : response[..Math.Min(response.Length, 400)] + (response.Length > 400 ? "…" : "");
            _logger.LogWarning(ex, "[Разбор JSON] Ошибка. Превью ответа: {Preview}", preview);

            return new PhishingDecision
            {
                Verdict = "error",
                Confidence = 0,
                Reasons = new List<string> { "Не удалось распарсить ответ LLM" },
            };
        }
    }
}
