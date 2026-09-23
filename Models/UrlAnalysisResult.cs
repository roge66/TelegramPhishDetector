namespace TelegramPhishDetector.Models;

public class UrlAnalysisResult
{
    public string Url { get; set; } = "";
    public bool IsUnsafe { get; set; }
    public string? ThreatType { get; set; }
    public string? Error { get; set; }
}