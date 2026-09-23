using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Types;
using TelegramPhishDetector.Models;
using TelegramPhishDetector.Services;
using TelegramPhishDetector.Utils;

namespace TelegramPhishDetector.Services.Implementations;

public class VirusTotalFileScanner : IFileScanner
{
    private readonly HttpClient _httpClient;
    private readonly ITelegramBotClient _botClient;
    private readonly string _apiKey;

    public VirusTotalFileScanner(HttpClient httpClient, IConfiguration configuration, ITelegramBotClient botClient)
    {
        _httpClient = httpClient;
        _botClient = botClient;
        _httpClient.BaseAddress = new Uri("https://www.virustotal.com/api/v3/");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _apiKey = configuration["VirusTotalApiKey"]
                  ?? throw new InvalidOperationException("Не задан VirusTotalApiKey в конфигурации.");
    }

    public async Task<FileAnalysisResult> ScanFileAsync(Document document, CancellationToken cancellationToken)
    {
        var bytes = await TelegramDownloader.DownloadFileAsync(_botClient, document.FileId, cancellationToken).ConfigureAwait(false);
        var hash = SHA256.HashData(bytes);
        var sha256Hex = Convert.ToHexString(hash).ToLowerInvariant();

        var fileName = string.IsNullOrEmpty(document.FileName) ? $"file_{sha256Hex[..Math.Min(8, sha256Hex.Length)]}.bin" : document.FileName;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"files/{sha256Hex}");
        request.Headers.Add("x-apikey", _apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new FileAnalysisResult
            {
                FileName = fileName,
                Sha256 = sha256Hex,
                Positives = 0,
                Total = 0,
                Permalink = null,
            };
        }

        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var root = json.RootElement;
        var analysis = JsonOptional(root, "data", "attributes", "last_analysis_stats");

        var malicious = GetInt(analysis, "malicious");
        var suspicious = GetInt(analysis, "suspicious");
        var harmless = GetInt(analysis, "harmless");
        var undetected = GetInt(analysis, "undetected");
        var timedOut = GetInt(analysis, "timeout");
        var total = malicious + suspicious + harmless + undetected + timedOut;

        var permalinkProp = JsonOptional(root, "data", "links", "self");
        var permalink = permalinkProp.ValueKind == JsonValueKind.String ? permalinkProp.GetString() : null;

        return new FileAnalysisResult
        {
            FileName = fileName,
            Sha256 = sha256Hex,
            Positives = malicious + suspicious,
            Total = total,
            Permalink = permalink,
        };
    }

    private static JsonElement JsonOptional(JsonElement current, params string[] pathSegments)
    {
        foreach (var segment in pathSegments)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return default;
        }

        return current;
    }

    private static int GetInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var prop))
            return 0;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetInt32(out var i) ? i : 0,
            JsonValueKind.String => int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0,
            _ => 0,
        };
    }
}
