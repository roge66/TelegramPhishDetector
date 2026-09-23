namespace TelegramPhishDetector.Models;

public class FileAnalysisResult
{
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int Positives { get; set; }
    public int Total { get; set; }
    public string? Permalink { get; set; }
    public bool IsMalicious => Positives > 0;
}