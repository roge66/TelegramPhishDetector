using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Telegram.Bot;
using Telegram.Bot.Types;
using TelegramPhishDetector.Models;

namespace TelegramPhishDetector.Modules;

public class MessageHandlerModule
{
    private readonly ITelegramBotClient _bot;
    private readonly DecisionModule _decision;
    private readonly ILogger<MessageHandlerModule> _logger;

    public MessageHandlerModule(
        ITelegramBotClient bot,
        DecisionModule decision,
        ILogger<MessageHandlerModule> logger)
    {
        _bot = bot;
        _decision = decision;
        _logger = logger;
    }

    public async Task HandleUpdateAsync(Update update, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (update.Message == null)
        {
            _logger.LogDebug("Update {UpdateId} пропущен: поле Message отсутствует", update.Id);
            return;
        }

        var msg = update.Message;
        _logger.LogInformation(
            "Начата обработка сообщения {MessageId}: чат={ChatId}, тип={MessageType}",
            msg.Id,
            msg.Chat.Id,
            msg.Type);

        // Проверяем только сообщения, которые имеют текстовый или медиа-контент
        if (msg.Text == null && msg.Voice == null && msg.Document == null && msg.Photo == null && msg.Contact == null)
        {
            _logger.LogInformation(
                "Сообщение {MessageId} пропущено: нет анализируемого контента (текст, голос, документ, фото, контакт)",
                msg.Id);
            return;
        }

        _logger.LogInformation("Начато решение для сообщения {MessageId} в чате {ChatId}", msg.Id, msg.Chat.Id);

        PhishingDecision decision;
        try
        {
            decision = await _decision.DecideAsync(msg, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Исключение при принятии решения для сообщения {MessageId}", msg.Id);
            return;
        }

        _logger.LogInformation(
            "Решение готово: вердикт={Verdict}, уверенность={Confidence:P0}, причин={ReasonsCount}",
            decision.Verdict,
            decision.Confidence,
            decision.Reasons.Count);

        if (decision.Verdict != "legitimate")
        {
            var reply = $"⚠️ *{decision.Verdict.ToUpper()}* (уверенность {decision.Confidence:P0})\n" +
                        $"Причины: {string.Join(", ", decision.Reasons)}";
            try
            {
                _logger.LogInformation(
                    "Начата отправка предупреждения пользователю: сообщение={MessageId}, чат={ChatId}, длина ответа={ReplyLen}",
                    msg.Id,
                    msg.Chat.Id,
                    reply.Length);
                await _bot.SendMessage(
                        msg.Chat.Id,
                        reply,
                        parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                        cancellationToken: ct)
                    .ConfigureAwait(false);
                _logger.LogInformation("Отправлен ответ пользователю для сообщения {MessageId}", msg.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось отправить предупреждение в чат для сообщения {MessageId}", msg.Id);
            }
        }
        else
            _logger.LogInformation("Ответ пользователю не требуется (вердикт legitimate), сообщение {MessageId}", msg.Id);

        sw.Stop();
        _logger.LogInformation(
            "Обработка сообщения {MessageId} завершена за {ElapsedMs} мс",
            msg.Id,
            sw.ElapsedMilliseconds);
    }
}
