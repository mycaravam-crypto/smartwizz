using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartWizz;

public sealed record Completion(string Content, string Model, int? PromptTokens = null, int? CompletionTokens = null);

// OpenAI-compatible chat-completions client. Works with hosted and local servers (Ollama, vLLM, LM Studio, ...).
// Overload answers (429/5xx) and dropped connections are retried, then the next fallback model is tried.
// Everything else fails at once, so a wrong key or model name is never masked by a fallback.
public sealed class Provider(
    HttpClient http, ProviderConfig cfg, string? apiKey,
    TextWriter? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    static readonly HashSet<int> RetryableStatuses = [429, 500, 502, 503, 504];
    static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    sealed record Attempt(string? Content, SmartWizzException? Error, bool Retryable, TimeSpan? RetryAfter,
        int? PromptTokens = null, int? CompletionTokens = null);

    public async Task<Completion> CompleteAsync(string system, string user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.Endpoint) || string.IsNullOrWhiteSpace(cfg.Model))
            throw new SmartWizzException(FailureKind.Configuration, "provider.endpoint and provider.model must be configured");

        string[] models = [cfg.Model, .. cfg.FallbackModels];
        SmartWizzException? lastError = null;
        for (int m = 0; m < models.Length; m++)
        {
            for (int attempt = 0; attempt <= cfg.MaxRetries; attempt++)
            {
                Attempt result = await SendOnceAsync(cfg.Endpoint, models[m], system, user, ct);
                if (result.Content is not null)
                    return new Completion(result.Content, models[m], result.PromptTokens, result.CompletionTokens);

                lastError = result.Error!;
                if (!result.Retryable)
                    throw lastError;
                if (attempt < cfg.MaxRetries)
                {
                    TimeSpan wait = result.RetryAfter ?? TimeSpan.FromSeconds(2 << attempt);
                    if (wait > MaxRetryDelay) wait = MaxRetryDelay;
                    log?.WriteLine($"provider: {models[m]}: {lastError.Message}; retry {attempt + 1}/{cfg.MaxRetries} in {wait.TotalSeconds:0}s");
                    await _delay(wait, ct);
                }
            }
            if (m + 1 < models.Length)
                log?.WriteLine($"provider: {models[m]} unavailable, falling back to {models[m + 1]}");
        }
        throw new SmartWizzException(FailureKind.Provider,
            $"all models unavailable ({string.Join(", ", models)}): {lastError!.Message}", lastError);
    }

    async Task<Attempt> SendOnceAsync(string endpoint, string model, string system, string user, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            // Buffered (not JsonContent) so Content-Length is set; some servers reject chunked bodies.
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model,
                temperature = cfg.Temperature,
                messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            }, Json), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            if (cfg.ApiKeyHeader is null)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            else if (!req.Headers.TryAddWithoutValidation(cfg.ApiKeyHeader, apiKey))
                throw new SmartWizzException(FailureKind.Configuration, $"provider.apiKeyHeader is not a valid request header: {cfg.ApiKeyHeader}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        int status;
        string body;
        TimeSpan? retryAfter;
        try
        {
            using HttpResponseMessage res = await http.SendAsync(req, cts.Token);
            status = (int)res.StatusCode;
            retryAfter = RetryAfter(res.Headers.RetryAfter);
            body = await res.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            // Not retried: the full timeout has already been spent.
            throw new SmartWizzException(FailureKind.Timeout, $"provider timed out after {cfg.TimeoutSeconds}s", e);
        }
        catch (HttpRequestException e)
        {
            return new Attempt(null, new SmartWizzException(FailureKind.Provider, $"provider request failed: {e.Message}", e), Retryable: true, null);
        }

        if (status is < 200 or > 299)
        {
            var error = new SmartWizzException(FailureKind.Provider, $"provider returned {status}: {Truncate(body, 500)}");
            return new Attempt(null, error, RetryableStatuses.Contains(status), retryAfter);
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            string? content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            int? promptTokens = null;
            int? completionTokens = null;
            if (doc.RootElement.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out JsonElement prompt) && prompt.TryGetInt32(out int promptCount))
                    promptTokens = promptCount;
                if (usage.TryGetProperty("completion_tokens", out JsonElement completion) && completion.TryGetInt32(out int completionCount))
                    completionTokens = completionCount;
            }
            return string.IsNullOrWhiteSpace(content)
                ? throw new SmartWizzException(FailureKind.Provider, "provider returned empty content")
                : new Attempt(content, null, Retryable: false, null, promptTokens, completionTokens);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new SmartWizzException(FailureKind.Provider, $"provider returned unexpected response: {Truncate(body, 500)}", e);
        }
    }

    // Seconds or an HTTP date; anything in the past counts as "no hint".
    static TimeSpan? RetryAfter(RetryConditionHeaderValue? header)
    {
        TimeSpan? wait = header?.Delta ?? (header?.Date - DateTimeOffset.UtcNow);
        return wait > TimeSpan.Zero ? wait : null;
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
