using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;

namespace TelegramPhishDetector.Utils;

public static class TelegramDownloader
{
    public static async Task<byte[]> DownloadFileAsync(ITelegramBotClient bot, string fileId, CancellationToken ct)
    {
        await using var memoryStream = new MemoryStream();
        await bot.GetInfoAndDownloadFile(fileId, memoryStream, cancellationToken: ct);
        return memoryStream.ToArray();
    }
}