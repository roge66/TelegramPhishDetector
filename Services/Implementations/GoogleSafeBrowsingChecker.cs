using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using TelegramPhishDetector.Models;

namespace TelegramPhishDetector.Services.Implementations;

public class GoogleSafeBrowsingChecker : IUrlSafetyChecker
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public GoogleSafeBrowsingChecker(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _apiKey = config["GoogleSafeBrowsingApiKey"]!;
    }

    public async Task<UrlAnalysisResult> CheckUrlAsync(string url, CancellationToken ct)
    {
        var request = new
        {
            client = new { clientId = "phish-detector", clientVersion = "1.0" },
            threatInfo = new
            {
                threatTypes = new[] { "MALWARE", "SOCIAL_ENGINEERING", "UNWANTED_SOFTWARE", "POTENTIALLY_HARMFUL_APPLICATION" },
                platformTypes = new[] { "ANY_PLATFORM" },
                threatEntryTypes = new[] { "URL" },
                threatEntries = new[] { new { url } }
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"https://safebrowsing.googleapis.com/v4/threatMatches:find?key={_apiKey}",
            request, ct);

        if (!response.IsSuccessStatusCode)
        {
            return new UrlAnalysisResult { Url = url, Error = response.StatusCode.ToString() };
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        bool isUnsafe = json.TryGetProperty("matches", out var matches) && matches.GetArrayLength() > 0;
        string? threatType = null;
        if (isUnsafe)
            threatType = matches[0].GetProperty("threatType").GetString();

        return new UrlAnalysisResult
        {
            Url = url,
            IsUnsafe = isUnsafe,
            ThreatType = threatType
        };
    }
}