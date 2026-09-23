using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using TelegramPhishDetector.Utils;

namespace TelegramPhishDetector.Services.Implementations;

public sealed class PaddleOcrEngine
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<PaddleOcrEngine> _logger;
    private readonly string _pythonPath;
    private readonly string _scriptPath;
    private readonly int _timeoutSeconds;

    public PaddleOcrEngine(
        ITelegramBotClient botClient,
        IConfiguration configuration,
        ILogger<PaddleOcrEngine> logger)
    {
        _botClient = botClient;
        _logger = logger;
        _pythonPath = ResolveExecutablePath(configuration["Ocr:Paddle:PythonPath"] ?? "py");
        _scriptPath = ResolvePath(configuration["Ocr:Paddle:ScriptPath"] ?? "Resources/ocr/paddle_ocr.py");
        _timeoutSeconds = int.TryParse(configuration["Ocr:Paddle:TimeoutSeconds"], out var timeout)
            ? Math.Max(timeout, 5)
            : 60;
    }

    public async Task<string> ExtractTextAsync(PhotoSize photo, CancellationToken cancellationToken)
    {
        if (!File.Exists(_scriptPath))
            throw new FileNotFoundException($"Не найден скрипт PaddleOCR: {_scriptPath}");

        var imagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
        try
        {
            var imageBytes = await TelegramDownloader.DownloadFileAsync(_botClient, photo.FileId, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(imagePath, imageBytes, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "[OCR:Paddle] Фото скачано: fileId={FileId}, bytes={Bytes}, telegramSize={Width}x{Height}, temp={TempFile}",
                photo.FileId,
                imageBytes.Length,
                photo.Width,
                photo.Height,
                imagePath);

            var result = await RunPaddleAsync(imagePath, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "[OCR:Paddle] Результат: confidence={Confidence:P1}, lines={Lines}, chars={Chars}, text={Text}",
                result.Confidence,
                result.Lines.Count,
                result.Text.Length,
                Truncate(result.Text, 1000));

            return result.Text;
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    private async Task<PaddleOcrResult> RunPaddleAsync(string imagePath, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["FLAGS_use_mkldnn"] = "0";
        psi.Environment["FLAGS_enable_pir_api"] = "0";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.ArgumentList.Add(_scriptPath);
        psi.ArgumentList.Add(imagePath);

        _logger.LogInformation(
            "[OCR:Paddle] Запуск: {Python} {Script} {Image}",
            _pythonPath,
            _scriptPath,
            imagePath);

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("Не удалось запустить процесс PaddleOCR.");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(proc);
            throw new TimeoutException($"PaddleOCR не завершился за {_timeoutSeconds} секунд.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogInformation("[OCR:Paddle] stderr: {Stderr}", Truncate(stderr.Trim(), 2000));

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"PaddleOCR завершился с кодом {proc.ExitCode}. stdout: {Truncate(stdout, 1000)} stderr: {Truncate(stderr, 1000)}");

        var result = JsonSerializer.Deserialize<PaddleOcrResult>(
            stdout,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return result ?? new PaddleOcrResult();
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        var currentDirPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), normalized));
        if (File.Exists(currentDirPath))
            return currentDirPath;

        var appBasePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, normalized));
        if (File.Exists(appBasePath))
            return appBasePath;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir.FullName, normalized));
            if (File.Exists(candidate))
                return candidate;
        }

        return appBasePath;
    }

    private static string ResolveExecutablePath(string path)
    {
        if (!LooksLikePath(path))
            return path;

        return ResolvePath(path);
    }

    private static bool LooksLikePath(string value) =>
        value.Contains('\\') || value.Contains('/') || Path.IsPathRooted(value);

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Процесс уже мог завершиться.
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
            // Временный файл не критичен.
        }
    }

    private sealed class PaddleOcrResult
    {
        public string Text { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public List<string> Lines { get; set; } = new();
    }
}
