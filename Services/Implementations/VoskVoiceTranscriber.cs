using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using TelegramPhishDetector.Utils;
using Vosk;

namespace TelegramPhishDetector.Services.Implementations;

/// <summary>
/// Расшифровка голосовых Telegram (OGG/Opus) через FFmpeg в WAV 16 кГц mono и офлайн-модель Vosk.
/// FFmpeg: встроенная копия из NuGet (ffmpeg\bin\ffmpeg.exe), либо PATH, либо явный <c>FfmpegPath</c>, либо ffmpeg.exe рядом с приложением.
/// </summary>
public sealed class VoskVoiceTranscriber : IVoiceTranscriber, IDisposable
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<VoskVoiceTranscriber> _logger;
    private readonly string _ffmpegExe;
    private readonly Model _model;
    private static int _voskLoggingConfigured;

    public VoskVoiceTranscriber(
        ITelegramBotClient botClient,
        IConfiguration configuration,
        ILogger<VoskVoiceTranscriber> logger)
    {
        _botClient = botClient;
        _logger = logger;
        var ffmpegCfg = configuration["FfmpegPath"];
        _ffmpegExe = ResolveFfmpegExecutable(
            string.IsNullOrWhiteSpace(ffmpegCfg) ? null : ffmpegCfg.Trim(),
            _logger);
        _logger.LogInformation("Для голосовых сообщений будет использоваться FFmpeg: {FfmpegPath}", _ffmpegExe);

        var modelRelative = configuration["VoskModelPath"]
                            ?? throw new InvalidOperationException("Не задан VoskModelPath в конфигурации.");

        var modelPath = Path.IsPathRooted(modelRelative)
            ? modelRelative
            : Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, modelRelative.Replace('/', Path.DirectorySeparatorChar)));

        if (!Directory.Exists(modelPath))
            throw new DirectoryNotFoundException($"Не найдена модель Vosk: {modelPath}");

        if (Interlocked.Exchange(ref _voskLoggingConfigured, 1) == 0)
            Vosk.Vosk.SetLogLevel(-1);

        _model = new Model(modelPath);
    }

    public async Task<string> TranscribeAsync(Voice voice, CancellationToken cancellationToken)
    {
        byte[] oggBytes;
        try
        {
            oggBytes = await TelegramDownloader.DownloadFileAsync(_botClient, voice.FileId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось загрузить голосовое сообщение");
            return string.Empty;
        }

        var tempOgg = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.oga");
        var tempWav = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(tempOgg, oggBytes, cancellationToken).ConfigureAwait(false);
            var ffmpegOk = await TryConvertWithFfmpegAsync(tempOgg, tempWav, cancellationToken).ConfigureAwait(false);
            if (!ffmpegOk)
                return string.Empty;

            return TranscribeWavFile(tempWav);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка при распознавании голосового сообщения");
            return string.Empty;
        }
        finally
        {
            TryDelete(tempOgg);
            TryDelete(tempWav);
        }
    }

    private async Task<bool> TryConvertWithFfmpegAsync(
        string sourcePath,
        string destPath,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegExe,
                Arguments = $"-y -hide_banner -loglevel error -i \"{sourcePath}\" -ar 16000 -ac 1 -f wav \"{destPath}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var ffmpegDir = Path.GetDirectoryName(_ffmpegExe);
            if (!string.IsNullOrEmpty(ffmpegDir) && Directory.Exists(ffmpegDir))
                psi.WorkingDirectory = ffmpegDir;

            using var proc = Process.Start(psi)!;

            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("FFmpeg завершился с кодом {Code}: {Err}", proc.ExitCode, stderr.Trim());
                return false;
            }

            return File.Exists(destPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is System.ComponentModel.Win32Exception w32 && w32.NativeErrorCode == 2)
            {
                _logger.LogWarning(
                    "FFmpeg не найден по пути «{Ffmpeg}». Ожидается встроенная копия: {Bundled}; " +
                    "либо установите FFmpeg (например: winget install ffmpeg), укажите полный путь в appsettings: FfmpegPath, " +
                    "либо положите ffmpeg.exe в папку с приложением ({Base}).",
                    _ffmpegExe,
                    Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
                    AppContext.BaseDirectory);
            }
            else
                _logger.LogWarning(ex, "Не удалось выполнить конвертацию через FFmpeg.");
            return false;
        }
    }

    private string TranscribeWavFile(string wavPath)
    {
        using var rec = new VoskRecognizer(_model, 16000f);
        rec.SetWords(false);

        using var source = File.OpenRead(wavPath);
        var buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            rec.AcceptWaveform(buffer, bytesRead);
        }

        var finalJson = rec.FinalResult();
        try
        {
            using var doc = JsonDocument.Parse(finalJson);
            return doc.RootElement.TryGetProperty("text", out var text)
                ? text.GetString() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsGenericFfmpegName(string value)
    {
        var t = value.Trim();
        return t.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase)
               || t.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveFfmpegExecutable(string? configured, ILogger logger)
    {
        // Явный абсолютный путь из конфига
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var c = configured.Trim();
            if (Path.IsPathRooted(c))
            {
                if (File.Exists(c))
                    return c;
                if (OperatingSystem.IsWindows() && !c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var withExe = c + ".exe";
                    if (File.Exists(withExe))
                        return withExe;
                }

                logger.LogWarning("FfmpegPath указывает на несуществующий файл: {Path}", c);
                return c;
            }
        }

        // NuGet FFMpegInstaller.Windows.x64 копирует в выход: ffmpeg\bin\ffmpeg.exe
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
        if (File.Exists(bundled))
        {
            logger.LogInformation("Используется встроенный FFmpeg: {Path}", bundled);
            return bundled;
        }

        // Относительный путь от каталога приложения (не имя «ffmpeg» из PATH)
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var c = configured.Trim();
            if (!Path.IsPathRooted(c) && !IsGenericFfmpegName(c))
            {
                var relative = Path.GetFullPath(
                    Path.Combine(AppContext.BaseDirectory, c.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(relative))
                    return relative;
            }
        }

        var sibling = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(sibling))
            return sibling;

        if (OperatingSystem.IsWindows())
        {
            var fromPath = TryResolveFfmpegFromWhere();
            if (!string.IsNullOrEmpty(fromPath))
                return fromPath;
        }

        var raw = string.IsNullOrWhiteSpace(configured) || IsGenericFfmpegName(configured)
            ? "ffmpeg"
            : configured.Trim();

        if (OperatingSystem.IsWindows() && !raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return raw + ".exe";

        return raw;
    }

    /// <summary>
    /// Ищет ffmpeg в каталогах PATH через стандартную утилиту where.exe.
    /// </summary>
    private static string? TryResolveFfmpegFromWhere()
    {
        foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
        {
            var line = RunWhereFirstLine(name);
            if (!string.IsNullOrEmpty(line) && File.Exists(line))
                return line;
        }

        return null;
    }

    private static string? RunWhereFirstLine(string executableName)
    {
        try
        {
            var wherePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "where.exe");
            if (!File.Exists(wherePath))
                return null;

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = wherePath,
                ArgumentList = { executableName },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (proc == null)
                return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0)
                return null;

            var first = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .FirstOrDefault(s => s.Length > 0);
            return first;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // временный файл — игнорируем
        }
    }

    public void Dispose()
    {
        _model.Dispose();
    }
}
