using Telegram.Bot.Types;
using TelegramPhishDetector.Models;

namespace TelegramPhishDetector.Services;

public interface IFileScanner
{
    Task<FileAnalysisResult> ScanFileAsync(Document document, CancellationToken cancellationToken);
}
