namespace TelegramPhishDetector.Services;

public interface ILlmClient
{
    Task<string> GetCompletionAsync(string prompt, CancellationToken cancellationToken);
}