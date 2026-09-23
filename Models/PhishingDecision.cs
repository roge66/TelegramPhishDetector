using System.Collections.Generic;

namespace TelegramPhishDetector.Models;

public class PhishingDecision
{
    public string Verdict { get; set; } = "legitimate";
    public double Confidence { get; set; }
    public List<string> Reasons { get; set; } = new();
    public List<string> SuspiciousPhrases { get; set; } = new();
}