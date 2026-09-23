using System.Collections.Generic;

namespace TelegramPhishDetector.Models;

public class MessageContext
{
    public string? Text { get; set; }
    public string? Caption { get; set; }
    public List<UrlAnalysisResult> Urls { get; set; } = new();
    public List<FileAnalysisResult> Files { get; set; } = new();
    public string? VoiceTranscription { get; set; }
    public string? OcrText { get; set; }
    public ContactInfo? Contact { get; set; }
    public bool HasUnprocessedPhoto { get; set; }
}