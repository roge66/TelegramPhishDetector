using TelegramPhishDetector.Models;

public interface IUrlSafetyChecker
{
    Task<UrlAnalysisResult> CheckUrlAsync(string url, CancellationToken cancellationToken);
}