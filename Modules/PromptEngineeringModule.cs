using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using TelegramPhishDetector.Models;
using TelegramPhishDetector.Services;

namespace TelegramPhishDetector.Modules;

public class PromptEngineeringModule
{
    private readonly ITextNormalizer _normalizer;
    private readonly IUrlSafetyChecker _urlChecker;
    private readonly IFileScanner _fileScanner;
    private readonly IVoiceTranscriber _voiceTranscriber;
    private readonly IImageOcr _imageOcr;
    private readonly IContactParser _contactParser;
    private readonly string _promptTemplate;
    private readonly ILogger<PromptEngineeringModule> _logger;

    public PromptEngineeringModule(
        ITextNormalizer normalizer,
        IUrlSafetyChecker urlChecker,
        IFileScanner fileScanner,
        IVoiceTranscriber voiceTranscriber,
        IImageOcr imageOcr,
        IContactParser contactParser,
        ILogger<PromptEngineeringModule> logger)
    {
        _normalizer = normalizer;
        _urlChecker = urlChecker;
        _fileScanner = fileScanner;
        _voiceTranscriber = voiceTranscriber;
        _imageOcr = imageOcr;
        _contactParser = contactParser;
        _logger = logger;

        var templatePath = Path.Combine(AppContext.BaseDirectory, "Resources", "prompt_template.txt");
        _promptTemplate = File.Exists(templatePath)
            ? File.ReadAllText(templatePath)
            : throw new FileNotFoundException($"Не найден шаблон промпта: {templatePath}");
    }

    public async Task<string> BuildPromptAsync(Message msg, CancellationToken ct)
    {
        var swTotal = Stopwatch.StartNew();
        var ctx = new MessageContext();
        _logger.LogInformation(
            "[Контекст] Начато построение контекста сообщения {MessageId}: текст={HasText}, подпись={HasCaption}, голос={HasVoice}, документ={HasDocument}, фото={HasPhoto}, контакт={HasContact}",
            msg.Id,
            !string.IsNullOrEmpty(msg.Text),
            !string.IsNullOrEmpty(msg.Caption),
            msg.Voice != null,
            msg.Document != null,
            msg.Photo is { Length: > 0 },
            msg.Contact != null);

        // Ссылки извлекаем из ИСХОДНОГО текста до нормализации: иначе TranslitNormalizer
        // заменяет лат. "h" на кирил. "х" и ломает схему "http://" → "хttp://" (regex не находит URL).
        var rawForUrls = $"{msg.Text ?? ""} {msg.Caption ?? ""}";
        _logger.LogInformation("[Текст] Исходный текст пользователя: {Text}", Truncate(msg.Text, 1000));
        _logger.LogInformation("[Подпись] Исходная подпись пользователя: {Caption}", Truncate(msg.Caption, 1000));
        var urlsFromRaw = ExtractUrls(rawForUrls).Select(TrimUrlTrailingPunctuation).Where(u => u.Length > 0).ToList();
        _logger.LogInformation("[Ссылки] Из сырого текста извлечено URL: {RawCount}", urlsFromRaw.Count);

        // Текст и подпись
        try
        {
            _logger.LogInformation("[Текст/подпись] Начата нормализация");
            if (!string.IsNullOrEmpty(msg.Text))
            {
                ctx.Text = _normalizer.Normalize(msg.Text);
                _logger.LogInformation(
                    "[Текст] Нормализация успешна, длина до/после: {OriginalLen}/{NormalizedLen}",
                    msg.Text.Length,
                    ctx.Text.Length);
            }
            else
                _logger.LogDebug("[Текст] Нет текстового содержимого");

            if (!string.IsNullOrEmpty(msg.Caption))
            {
                ctx.Caption = _normalizer.Normalize(msg.Caption);
                _logger.LogInformation(
                    "[Подпись] Нормализация успешна, длина до/после: {OriginalLen}/{NormalizedLen}",
                    msg.Caption.Length,
                    ctx.Caption.Length);
            }
            else
                _logger.LogDebug("[Подпись] Нет подписи к медиа");

            _logger.LogInformation("[Текст/подпись] Нормализация завершена");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Текст/подпись] Ошибка нормализации");
            throw;
        }

        var fullText = (ctx.Text + " " + ctx.Caption).Trim();
        var urlsFromNormalized = ExtractUrls(fullText).Select(TrimUrlTrailingPunctuation).Where(u => u.Length > 0).ToList();
        var urls = MergeDistinctUrls(urlsFromRaw, urlsFromNormalized);
        _logger.LogInformation(
            "[Ссылки] К проверке: {Total} (из сырого текста: {RawCount}, после нормализации: {NormCount})",
            urls.Count,
            urlsFromRaw.Count,
            urlsFromNormalized.Count);
        foreach (var url in urls)
        {
            try
            {
                _logger.LogInformation("[Ссылки] Начата проверка URL: {Url}", Truncate(url, 120));
                var sw = Stopwatch.StartNew();
                var result = await _urlChecker.CheckUrlAsync(url, ct).ConfigureAwait(false);
                ctx.Urls.Add(result);
                if (!string.IsNullOrEmpty(result.Error))
                    _logger.LogWarning(
                        "[Ссылки] {Url}: проверка завершилась ошибкой провайдера после {ElapsedMs} мс: {Error}",
                        Truncate(url, 120),
                        sw.ElapsedMilliseconds,
                        result.Error);
                else
                    _logger.LogInformation(
                        "[Ссылки] {Url}: небезопасно={Unsafe}, тип угрозы={Threat}, за {ElapsedMs} мс",
                        Truncate(url, 120),
                        result.IsUnsafe,
                        result.ThreatType ?? "—",
                        sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Ссылки] Исключение при проверке {Url}", Truncate(url, 120));
                ctx.Urls.Add(new UrlAnalysisResult { Url = url, Error = ex.Message });
            }
        }
        _logger.LogInformation(
            "[Ссылки] Проверка завершена: проверено={Checked}, небезопасных={Unsafe}, с ошибками={Errors}",
            ctx.Urls.Count,
            ctx.Urls.Count(u => u.IsUnsafe && string.IsNullOrEmpty(u.Error)),
            ctx.Urls.Count(u => !string.IsNullOrEmpty(u.Error)));

        // Файлы (документы)
        if (msg.Document != null)
        {
            try
            {
                _logger.LogInformation(
                    "[Документ] Начато сканирование: имя={FileName}, размер={FileSize}, mime={MimeType}",
                    Truncate(msg.Document.FileName, 120),
                    msg.Document.FileSize,
                    msg.Document.MimeType ?? "—");
                var sw = Stopwatch.StartNew();
                var fileResult = await _fileScanner.ScanFileAsync(msg.Document, ct).ConfigureAwait(false);
                ctx.Files.Add(fileResult);
                _logger.LogInformation(
                    "[Документ] Скан за {ElapsedMs} мс: {FileName}, sha256 префикс={ShaPrefix}, positives={Pos}/{Total}, malicious={Malicious}",
                    sw.ElapsedMilliseconds,
                    Truncate(fileResult.FileName ?? "?", 80),
                    Truncate(fileResult.Sha256, 12),
                    fileResult.Positives,
                    fileResult.Total,
                    fileResult.IsMalicious);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Документ] Исключение при сканировании файла {FileId}", msg.Document.FileId);
            }
        }
        else
            _logger.LogDebug("[Документ] Документ отсутствует — сканирование пропущено");

        // Голосовые
        if (msg.Voice != null)
        {
            try
            {
                _logger.LogInformation(
                    "[Голос] Начата транскрибация: длительность={Duration} сек, размер={FileSize}, mime={MimeType}",
                    msg.Voice.Duration,
                    msg.Voice.FileSize,
                    msg.Voice.MimeType ?? "—");
                var sw = Stopwatch.StartNew();
                ctx.VoiceTranscription = await _voiceTranscriber.TranscribeAsync(msg.Voice, ct).ConfigureAwait(false);
                var len = ctx.VoiceTranscription?.Length ?? 0;
                if (len == 0)
                    _logger.LogInformation("[Голос] Расшифровка пустая за {ElapsedMs} мс", sw.ElapsedMilliseconds);
                else
                    _logger.LogInformation(
                        "[Голос] Расшифровка за {ElapsedMs} мс, длина текста={Len}, текст: {Text}",
                        sw.ElapsedMilliseconds,
                        len,
                        Truncate(ctx.VoiceTranscription, 1000));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Голос] Ошибка распознавания");
            }
        }
        else
            _logger.LogDebug("[Голос] Голосовое сообщение отсутствует — транскрибация пропущена");

        // Фото (OCR)
        if (msg.Photo != null && msg.Photo.Length > 0)
        {
            try
            {
                var photo = msg.Photo.Last();
                _logger.LogInformation(
                    "[OCR] Начато распознавание фото: вариантов={PhotoCount}, выбранный размер={Width}x{Height}, файл={FileId}",
                    msg.Photo.Length,
                    photo.Width,
                    photo.Height,
                    photo.FileId);
                var sw = Stopwatch.StartNew();
                ctx.OcrText = await _imageOcr.ExtractTextAsync(photo, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "[OCR] Извлечён текст за {ElapsedMs} мс, символов: {Len}, текст: {Text}",
                    sw.ElapsedMilliseconds,
                    ctx.OcrText?.Length ?? 0,
                    Truncate(ctx.OcrText, 1000));
            }
            catch (Exception ex)
            {
                ctx.HasUnprocessedPhoto = true;
                _logger.LogWarning(ex, "[OCR] Фото не обработано, флаг HasUnprocessedPhoto установлен");
            }
        }
        else
            _logger.LogDebug("[OCR] Фото отсутствует — распознавание пропущено");

        // Контакты
        if (msg.Contact != null)
        {
            try
            {
                _logger.LogInformation(
                    "[Контакт] Начат разбор контакта: имя={FirstName}, фамилия={LastName}, есть телефон={HasPhone}",
                    Truncate(msg.Contact.FirstName, 80),
                    Truncate(msg.Contact.LastName, 80),
                    !string.IsNullOrEmpty(msg.Contact.PhoneNumber));
                ctx.Contact = _contactParser.Parse(msg.Contact);
                _logger.LogInformation(
                    "[Контакт] Разобран: {Display}, есть телефон={HasPhone}",
                    Truncate($"{msg.Contact.FirstName} {msg.Contact.LastName}".Trim(), 80),
                    !string.IsNullOrEmpty(msg.Contact.PhoneNumber));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Контакт] Ошибка разбора контакта");
            }
        }
        else
            _logger.LogDebug("[Контакт] Контакт отсутствует — разбор пропущен");

        _logger.LogInformation(
            "[Контекст] Итог перед сериализацией: urls={UrlCount}, files={FileCount}, voiceTextLen={VoiceLen}, ocrTextLen={OcrLen}, contact={HasContact}, photoUnprocessed={PhotoUnprocessed}",
            ctx.Urls.Count,
            ctx.Files.Count,
            ctx.VoiceTranscription?.Length ?? 0,
            ctx.OcrText?.Length ?? 0,
            ctx.Contact != null,
            ctx.HasUnprocessedPhoto);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var ctxJson = JsonSerializer.Serialize(ctx, options);
        var finalPromptLength = _promptTemplate.Length + ctxJson.Length;
        swTotal.Stop();
        _logger.LogInformation(
            "[Контекст] Сериализация за {ElapsedMs} мс; JSON контекста {JsonLen} символов (~итого промпт {ApproxTotal})",
            swTotal.ElapsedMilliseconds,
            ctxJson.Length,
            finalPromptLength);

        return _promptTemplate.Replace("{{MESSAGE_CONTEXT}}", ctxJson);
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static List<string> MergeDistinctUrls(IEnumerable<string> primary, IEnumerable<string> secondary)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var u in primary)
        {
            if (string.IsNullOrWhiteSpace(u) || !seen.Add(u))
                continue;
            list.Add(u);
        }

        foreach (var u in secondary)
        {
            if (string.IsNullOrWhiteSpace(u) || !seen.Add(u))
                continue;
            list.Add(u);
        }

        return list;
    }

    /// <summary>
    /// Убирает типичные знаки конца предложения, попавшие в матч regex (например «...FOUND.»).
    /// </summary>
    private static string TrimUrlTrailingPunctuation(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var u = url;
        while (u.Length > 0 && IsUrlTrailingNoise(u[^1]))
            u = u[..^1];
        return u;
    }

    private static bool IsUrlTrailingNoise(char c) =>
        c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '"' or '\''
            or '»' or '”' or '’' or '。';

    private List<string> ExtractUrls(string text)
    {
        var regex = new Regex(@"https?:\/\/[^\s]+", RegexOptions.IgnoreCase);
        return regex.Matches(text).Select(m => m.Value).ToList();
    }
}
