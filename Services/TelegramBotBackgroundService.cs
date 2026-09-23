using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPhishDetector.Modules;

namespace TelegramPhishDetector;

public class TelegramBotBackgroundService : BackgroundService
{
    private readonly ITelegramBotClient _bot;
    private readonly ILogger<TelegramBotBackgroundService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public TelegramBotBackgroundService(
        ITelegramBotClient bot,
        ILogger<TelegramBotBackgroundService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _bot = bot;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Запуск Telegram бота...");
        var me = await _bot.GetMe(stoppingToken);
        _logger.LogInformation("Бот @{Username} запущен", me.Username);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>() // получать все типы обновлений
        };

        _bot.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken
        );

        // Бесконечное ожидание, пока не поступит сигнал остановки
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        var updateId = update.Id;

        try
        {
            if (update.Message is { } incoming)
            {
                using (_logger.BeginScope(
                    "Обработка UpdateId:{UpdateId} ChatId:{ChatId} MessageId:{MessageId}",
                    updateId,
                    incoming.Chat.Id,
                    incoming.Id))
                {
                    _logger.LogInformation(
                        "Получено сообщение: чат={ChatId}, сообщение={MessageId}, текст={HasText}, голос={HasVoice}, файл={HasDoc}, фото={HasPhoto}, контакт={HasContact}",
                        incoming.Chat.Id,
                        incoming.Id,
                        incoming.Text != null,
                        incoming.Voice != null,
                        incoming.Document != null,
                        incoming.Photo is { Length: > 0 },
                        incoming.Contact != null);

                    using var scope = _scopeFactory.CreateScope();
                    var handler = scope.ServiceProvider.GetRequiredService<MessageHandlerModule>();
                    await handler.HandleUpdateAsync(update, ct);
                }

                _logger.LogDebug("Обновление {UpdateId} обработано без исключений", updateId);
            }
            else
                _logger.LogDebug("Обновление {UpdateId}: нет поля Message — пропуск", updateId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сбой обработки Telegram update {UpdateId}", updateId);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        _logger.LogError(exception, "Ошибка при получении обновлений");
        return Task.CompletedTask;
    }
}