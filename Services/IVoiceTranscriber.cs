using Telegram.Bot.Types;

public interface IVoiceTranscriber
{
    Task<string> TranscribeAsync(Voice voice, CancellationToken cancellationToken);
}