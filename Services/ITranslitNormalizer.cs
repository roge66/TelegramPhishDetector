namespace TelegramPhishDetector.Services;

public interface ITranslitNormalizer
{
    string Normalize(string text);
}