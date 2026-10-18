using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartWizz;

// OpenAI-compatible chat-completions client. Works with hosted and local servers (Ollama, vLLM, LM Studio, ...).
public sealed class Provider(HttpClient http, ProviderConfig cfg, string? apiKey)
{
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.Endpoint) || string.IsNullOrWhiteSpace(cfg.Model))
            throw new SmartWizzException("provider.endpoint and provider.model must be configured");

        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Endpoint)
        {
            // Buffered (not JsonContent) so Content-Length is set; some servers reject chunked bodies.
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = cfg.Model,
                temperature = cfg.Temperature,
                messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            }, Json), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        int status;
        string body;
        try
        {
            using HttpResponseMessage res = await http.SendAsync(req, cts.Token);
            status = (int)res.StatusCode;
            body = await res.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SmartWizzException($"provider timed out after {cfg.TimeoutSeconds}s");
        }
        catch (HttpRequestException e)
        {
            throw new SmartWizzException($"provider request failed: {e.Message}");
        }

        if (status is < 200 or > 299)
            throw new SmartWizzException($"provider returned {status}: {Truncate(body, 500)}");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string? content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return string.IsNullOrWhiteSpace(content)
                ? throw new SmartWizzException("provider returned empty content")
                : content;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new SmartWizzException($"provider returned unexpected response: {Truncate(body, 500)}");
        }
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
