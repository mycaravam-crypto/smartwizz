using System.Net;
using System.Text.Json;

namespace SmartWizz.Tests;

public class RetryTests
{
    sealed class Recorder
    {
        public List<TimeSpan> Waits { get; } = [];
        public StringWriter Log { get; } = new();

        public Task Delay(TimeSpan wait, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Waits.Add(wait);
            return Task.CompletedTask;
        }
    }

    static ProviderConfig Cfg(params string[] fallbacks) =>
        new() { Endpoint = "http://llm/v1/chat/completions", Model = "primary", FallbackModels = fallbacks };

    static string ModelOf(string? body) => JsonDocument.Parse(body!).RootElement.GetProperty("model").GetString()!;

    static HttpResponseMessage Status(HttpStatusCode code, int? retryAfterSeconds = null)
    {
        HttpResponseMessage res = StubHandler.Json("""{"error":"busy"}""", code);
        if (retryAfterSeconds is { } s)
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return res;
    }

    [Fact]
    public async Task Overload_is_retried_with_backoff_then_succeeds()
    {
        var rec = new Recorder();
        int calls = 0;
        var h = new StubHandler((_, _) => ++calls < 3 ? Status(HttpStatusCode.ServiceUnavailable) : StubHandler.Chat("ok"));

        Completion c = await new Provider(new HttpClient(h), Cfg(), null, rec.Log, rec.Delay).CompleteAsync("s", "u", default);

        Assert.Equal(new Completion("ok", "primary"), c);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], rec.Waits);
        Assert.Contains("retry 1/2 in 2s", rec.Log.ToString());
    }

    [Theory]
    [InlineData(7, 7)]
    [InlineData(120, 30)] // capped
    public async Task Retry_after_header_is_honoured_and_capped(int header, int expectedSeconds)
    {
        var rec = new Recorder();
        int calls = 0;
        var h = new StubHandler((_, _) => ++calls == 1 ? Status(HttpStatusCode.TooManyRequests, header) : StubHandler.Chat("ok"));

        await new Provider(new HttpClient(h), Cfg(), null, rec.Log, rec.Delay).CompleteAsync("s", "u", default);

        Assert.Equal([TimeSpan.FromSeconds(expectedSeconds)], rec.Waits);
    }

    [Fact]
    public async Task Falls_back_to_next_model_after_retries_are_exhausted()
    {
        var rec = new Recorder();
        var h = new StubHandler((_, body) => ModelOf(body) == "primary" ? Status(HttpStatusCode.ServiceUnavailable) : StubHandler.Chat("ok"));

        Completion c = await new Provider(new HttpClient(h), Cfg("backup"), null, rec.Log, rec.Delay).CompleteAsync("s", "u", default);

        Assert.Equal(new Completion("ok", "backup"), c);
        Assert.Equal(["primary", "primary", "primary", "backup"], h.Requests.Select(r => ModelOf(r.Body)));
        Assert.Contains("primary unavailable, falling back to backup", rec.Log.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Configuration_errors_are_neither_retried_nor_masked_by_fallback(HttpStatusCode code)
    {
        var rec = new Recorder();
        var h = new StubHandler((_, _) => Status(code));

        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() =>
            new Provider(new HttpClient(h), Cfg("backup"), null, rec.Log, rec.Delay).CompleteAsync("s", "u", default));

        Assert.Single(h.Requests);
        Assert.Empty(rec.Waits);
        Assert.Contains(((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture), e.Message);
    }

    [Fact]
    public async Task All_models_exhausted_is_a_bounded_provider_failure()
    {
        var rec = new Recorder();
        var h = new StubHandler((_, _) => Status(HttpStatusCode.BadGateway));
        ProviderConfig cfg = Cfg("backup");
        cfg.MaxRetries = 1;

        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() =>
            new Provider(new HttpClient(h), cfg, null, rec.Log, rec.Delay).CompleteAsync("s", "u", default));

        Assert.Equal(FailureKind.Provider, e.Kind);
        Assert.Contains("all models unavailable (primary, backup)", e.Message);
        Assert.Equal(4, h.Requests.Count); // (1 + 1 retry) x 2 models
    }

    [Fact]
    public async Task Dropped_connection_is_retried()
    {
        int calls = 0;
        var h = new StubHandler((_, _) => ++calls == 1 ? throw new HttpRequestException("connection reset") : StubHandler.Chat("ok"));
        Completion c = await new Provider(new HttpClient(h), Cfg(), null, delay: StubHandler.NoDelay).CompleteAsync("s", "u", default);
        Assert.Equal("ok", c.Content);
    }

    [Fact]
    public async Task Timeout_is_not_retried()
    {
        var handler = new HangingHandler();
        ProviderConfig cfg = Cfg("backup");
        cfg.TimeoutSeconds = 1;
        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() =>
            new Provider(new HttpClient(handler), cfg, null, delay: StubHandler.NoDelay).CompleteAsync("s", "u", default));
        Assert.Equal(FailureKind.Timeout, e.Kind);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Cancellation_during_backoff_stops_retrying()
    {
        var h = new StubHandler((_, _) => Status(HttpStatusCode.ServiceUnavailable));
        using var cts = new CancellationTokenSource();
        var provider = new Provider(new HttpClient(h), Cfg("backup"), null, delay: (_, ct) =>
        {
            cts.Cancel();
            return Task.Delay(Timeout.Infinite, ct);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync("s", "u", cts.Token));
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Review_reports_which_models_answered()
    {
        int calls = 0;
        // File a.cs: primary answers. File b.cs: primary is overloaded, backup answers.
        var h = new StubHandler((_, body) => ModelOf(body) == "primary" && ++calls > 1
            ? Status(HttpStatusCode.ServiceUnavailable)
            : StubHandler.Chat("""{"findings":[]}"""));
        var provider = new Provider(new HttpClient(h), Cfg("backup"), null, delay: StubHandler.NoDelay);
        List<FileDiff> files = GitDiff.Parse("""
            diff --git a/a.cs b/a.cs
            --- a/a.cs
            +++ b/a.cs
            @@ -1,0 +1,1 @@
            +a
            diff --git a/b.cs b/b.cs
            --- a/b.cs
            +++ b/b.cs
            @@ -1,0 +1,1 @@
            +b

            """);

        ReviewResult result = await Review.RunAsync(files, new ReviewConfig { MaxCharsPerRequest = 40 }, provider, "sys", default);

        Assert.Equal(["primary", "backup"], result.Models);
        var w = new StringWriter();
        Report.WriteConsole(result, w);
        Assert.Contains("Reviewed by: primary, backup", w.ToString());
        Assert.Contains("\"models\"", Report.ToJson(result));
    }

    [Theory]
    [InlineData("""{ "provider": { "maxRetries": -1 } }""")]
    [InlineData("""{ "provider": { "maxRetries": 6 } }""")]
    [InlineData("""{ "provider": { "fallbackModels": ["ok", " "] } }""")]
    public void Invalid_retry_settings_fail_at_startup(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        try
        {
            Assert.Equal(FailureKind.Configuration, Assert.Throws<SmartWizzException>(() => Config.Load(path, _ => null)).Kind);
        }
        finally { File.Delete(path); }
    }
}
