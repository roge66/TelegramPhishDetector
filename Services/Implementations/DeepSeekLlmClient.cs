using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace TelegramPhishDetector.Services.Implementations;

public class DeepSeekLlmClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private const string BaseUrl = "https://api.deepseek.com/v1";

    public DeepSeekLlmClient(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _apiKey = config["DeepSeekApiKey"]!;
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    public async Task<string> GetCompletionAsync(string prompt, CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model = "deepseek-chat",
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = 500,
            temperature = 0.1,
            response_format = new { type = "json_object" }
        };

        var response = await _httpClient.PostAsJsonAsync("chat/completions", requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var content = json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return content ?? "{}";
    }
}