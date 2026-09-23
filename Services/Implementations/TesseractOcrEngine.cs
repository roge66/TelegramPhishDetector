using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tesseract;
using Telegram.Bot;
using Telegram.Bot.Types;
using TelegramPhishDetector.Utils;

namespace TelegramPhishDetector.Services.Implementations;

public class TesseractOcrEngine : IImageOcr
{
    private readonly TesseractEngine _engine;
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<TesseractOcrEngine> _logger;
    private readonly object _ocrLock = new();

    public TesseractOcrEngine(
        ITelegramBotClient botClient,
        IConfiguration config,
        ILogger<TesseractOcrEngine> logger)
    {
        _botClient = botClient;
        _logger = logger;
        var tessDataPath = config["TesseractDataPath"]
                           ?? throw new InvalidOperationException("Не задан TesseractDataPath в конфигурации.");

        if (!Path.IsPathRooted(tessDataPath))
        {
            tessDataPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    tessDataPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        if (!Directory.Exists(tessDataPath))
            throw new DirectoryNotFoundException($"Tesseract data path not found: {tessDataPath}");
        _engine = new TesseractEngine(tessDataPath, "rus+eng", EngineMode.Default);
    }

    public async Task<string> ExtractTextAsync(PhotoSize photo, CancellationToken cancellationToken)
    {
        var imageBytes = await TelegramDownloader.DownloadFileAsync(_botClient, photo.FileId, cancellationToken);
        _logger.LogInformation(
            "[OCR:Tesseract] Фото скачано: fileId={FileId}, bytes={Bytes}, telegramSize={Width}x{Height}",
            photo.FileId,
            imageBytes.Length,
            photo.Width,
            photo.Height);

        using var img = Pix.LoadFromMemory(imageBytes);

        var best = OcrCandidate.Empty;
        best = SelectBest(best, RunOcrAttempt(img, "original/auto", PageSegMode.Auto));
        best = SelectBest(best, RunOcrAttempt(img, "original/sparse", PageSegMode.SparseText));
        best = SelectBest(best, RunOcrAttempt(img, "original/block", PageSegMode.SingleBlock));

        TryWithPix("scaled2x", () => img.Scale(2.0f, 2.0f), pix =>
        {
            best = SelectBest(best, RunOcrAttempt(pix, "scaled2x/sparse", PageSegMode.SparseText));
            best = SelectBest(best, RunOcrAttempt(pix, "scaled2x/block", PageSegMode.SingleBlock));
        });

        TryWithPix("gray", () => img.ConvertRGBToGray(), gray =>
        {
            best = SelectBest(best, RunOcrAttempt(gray, "gray/sparse", PageSegMode.SparseText));

            TryWithPix("gray-scaled2x", () => gray.Scale(2.0f, 2.0f), grayScaled =>
            {
                best = SelectBest(best, RunOcrAttempt(grayScaled, "gray-scaled2x/sparse", PageSegMode.SparseText));

                TryWithPix(
                    "gray-scaled2x-otsu",
                    () => grayScaled.BinarizeOtsuAdaptiveThreshold(2000, 2000, 0, 0, 0.1f),
                    bin =>
                    {
                        best = SelectBest(best, RunOcrAttempt(bin, "gray-scaled2x-otsu/sparse", PageSegMode.SparseText));
                        best = SelectBest(best, RunOcrAttempt(bin, "gray-scaled2x-otsu/block", PageSegMode.SingleBlock));
                    });

                TryWithPix("gray-scaled2x-sauvola", () => grayScaled.BinarizeSauvola(25, 0.35f, true), bin =>
                {
                    best = SelectBest(best, RunOcrAttempt(bin, "gray-scaled2x-sauvola/sparse", PageSegMode.SparseText));
                    best = SelectBest(best, RunOcrAttempt(bin, "gray-scaled2x-sauvola/block", PageSegMode.SingleBlock));
                });
            });
        });

        _logger.LogInformation(
            "[OCR:Tesseract] Лучший результат: attempt={Attempt}, confidence={Confidence:P1}, chars={Length}, text={Text}",
            best.Attempt,
            best.Confidence,
            best.Text.Length,
            Truncate(best.Text, 1000));

        return best.Text;
    }

    private OcrCandidate RunOcrAttempt(Pix pix, string attempt, PageSegMode mode)
    {
        try
        {
            Page page;
            lock (_ocrLock)
            {
                page = _engine.Process(pix, mode);
            }

            using (page)
            {
                var text = NormalizeOcrText(page.GetText());
                var confidence = page.GetMeanConfidence();
                _logger.LogInformation(
                    "[OCR:Tesseract] Попытка {Attempt}: confidence={Confidence:P1}, chars={Length}, text={Text}",
                    attempt,
                    confidence,
                    text.Length,
                    Truncate(text, 300));
                return new OcrCandidate(attempt, text, confidence);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OCR:Tesseract] Попытка {Attempt} завершилась ошибкой", attempt);
            return OcrCandidate.Empty;
        }
    }

    private void TryWithPix(string label, Func<Pix> createPix, Action<Pix> action)
    {
        try
        {
            using var pix = createPix();
            action(pix);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OCR:Tesseract] Предобработка {Label} завершилась ошибкой", label);
        }
    }

    private static OcrCandidate SelectBest(OcrCandidate current, OcrCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Text))
            return current;
        if (string.IsNullOrWhiteSpace(current.Text))
            return candidate;

        var currentScore = current.Confidence + Math.Min(current.Text.Length, 500) / 10000f;
        var candidateScore = candidate.Confidence + Math.Min(candidate.Text.Length, 500) / 10000f;
        return candidateScore > currentScore ? candidate : current;
    }

    private static string NormalizeOcrText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return string.Join(
            "\n",
            text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        return s.Length <= max ? s : s[..max] + "…";
    }

    private readonly record struct OcrCandidate(string Attempt, string Text, float Confidence)
    {
        public static OcrCandidate Empty { get; } = new("none", string.Empty, 0f);
    }
}