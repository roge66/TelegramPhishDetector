namespace TelegramPhishDetector.Services;

public interface IHomoglyphNormalizer
{
    string Normalize(string text);
}