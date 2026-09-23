using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;

namespace TelegramPhishDetector.Services.Implementations;

public sealed class CompositeImageOcr : IImageOcr
{
    private readonly PaddleOcrEngine _paddle;
    private readonly TesseractOcrEngine _tesseract;
    private readonly ILogger<CompositeImageOcr> _logger;
    private readonly bool _usePaddle;
    private readonly bool _fallbackToTesseract;
    private readonly int _minPaddleChars;

    public CompositeImageOcr(
        PaddleOcrEngine paddle,
        TesseractOcrEngine tesseract,
        IConfiguration configuration,
        ILogger<CompositeImageOcr> logger)
    {
        _paddle = paddle;
        _tesseract = tesseract;
        _logger = logger;
        _usePaddle = !string.Equals(configuration["Ocr:Primary"], "Tesseract", StringComparison.OrdinalIgnoreCase);
        _fallbackToTesseract = !bool.TryParse(configuration["Ocr:FallbackToTesseract"], out var fallback) || fallback;
        _minPaddleChars = int.TryParse(configuration["Ocr:Paddle:MinCharsForSuccess"], out var minChars)
            ? Math.Max(minChars, 0)
            : 2;
    }

    public async Task<string> ExtractTextAsync(PhotoSize photo, CancellationToken cancellationToken)
    {
        if (_usePaddle)
        {
            try
            {
                _logger.LogInformation("[OCR] Основной движок: PaddleOCR");
                var text = await _paddle.ExtractTextAsync(photo, cancellationToken).ConfigureAwait(false);
                if (text.Trim().Length >= _minPaddleChars)
                {
                    _logger.LogInformation("[OCR] PaddleOCR дал пригодный результат: chars={Chars}", text.Trim().Length);
                    return text;
                }

                _logger.LogWarning(
                    "[OCR] PaddleOCR вернул слишком мало текста: chars={Chars}, min={MinChars}",
                    text.Trim().Length,
                    _minPaddleChars);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[OCR] PaddleOCR недоступен или завершился ошибкой");
            }

            if (!_fallbackToTesseract)
                return string.Empty;

            _logger.LogInformation("[OCR] Переход к fallback: Tesseract");
        }
        else
            _logger.LogInformation("[OCR] Основной движок: Tesseract");

        return await _tesseract.ExtractTextAsync(photo, cancellationToken).ConfigureAwait(false);
    }
}
