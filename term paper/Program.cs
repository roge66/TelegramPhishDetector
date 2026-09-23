using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using TelegramPhishDetector;
using TelegramPhishDetector.Modules;
using TelegramPhishDetector.Services;
using TelegramPhishDetector.Services.Implementations;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        // Telegram Bot
        services.AddSingleton<ITelegramBotClient>(_ =>
            new TelegramBotClient(
                context.Configuration["TelegramBotToken"]
                    ?? throw new InvalidOperationException("Не задан TelegramBotToken.")));

        // HTTP-клиенты для API
        services.AddHttpClient<ILlmClient, OpenRouterLlmClient>();
        services.AddHttpClient<IUrlSafetyChecker, GoogleSafeBrowsingChecker>();
        services.AddHttpClient<IFileScanner, VirusTotalFileScanner>();

        // Нормализаторы
        services.AddSingleton<IHomoglyphNormalizer, HomoglyphNormalizer>();
        services.AddSingleton<ITranslitNormalizer, TranslitNormalizer>();
        services.AddSingleton<ITextNormalizer, CompositeTextNormalizer>();

        // Остальные сервисы
        services.AddSingleton<IVoiceTranscriber, VoskVoiceTranscriber>();
        services.AddSingleton<PaddleOcrEngine>();
        services.AddSingleton<TesseractOcrEngine>();
        services.AddSingleton<IImageOcr, CompositeImageOcr>();
        services.AddSingleton<IContactParser, ContactParser>();

        // Модули (scoped)
        services.AddScoped<PromptEngineeringModule>();
        services.AddScoped<DecisionModule>();
        services.AddScoped<MessageHandlerModule>();

        // Фоновый сервис
        services.AddHostedService<TelegramBotBackgroundService>();
    })
    .Build();

await host.RunAsync();