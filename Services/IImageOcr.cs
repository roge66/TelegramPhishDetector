using Telegram.Bot.Types;

public interface IImageOcr
{
    Task<string> ExtractTextAsync(PhotoSize photo, CancellationToken cancellationToken);
}