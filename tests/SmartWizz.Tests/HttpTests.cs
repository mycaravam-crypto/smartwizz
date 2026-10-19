using System.Net;
using System.Text.Json;


namespace SmartWizz.Tests;

public class ProviderTests
{
    static Provider Make(StubHandler h, ProviderConfig? cfg = null) =>
        new(new HttpClient(h), cfg ?? new ProviderConfig { Endpoint = "http://llm/v1/chat/completions", Model = "m" }, "sk-secret",
            delay: StubHandler.NoDelay);

    [Fact]
    public async Task Sends_model_messages_and_bearer_key()
    {
        var h = new StubHandler((_, _) => StubHandler.Chat("ok"));
        Assert.Equal(new Completion("ok", "m"), await Make(h).CompleteAsync("sys", "usr", default));

        (HttpRequestMessage? req, string? body) = h.Requests.Single();
        Assert.Equal("Bearer sk-secret", req.Headers.Authorization!.ToString());
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("m", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal("usr", doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Reads_optional_provider_token_usage()
    {
        const string reply = """
            {"choices":[{"message":{"role":"assistant","content":"ok"}}],
             "usage":{"prompt_tokens":128,"completion_tokens":42}}
            """;
        var handler = new StubHandler((_, _) => StubHandler.Json(reply));
        Completion completion = await Make(handler).CompleteAsync("sys", "user", default);
        Assert.Equal(128, completion.PromptTokens);
        Assert.Equal(42, completion.CompletionTokens);
    }

    [Fact]
    public async Task Omits_temperature_when_null()
    {
        var h = new StubHandler((_, _) => StubHandler.Chat("ok"));
        await Make(h, new ProviderConfig { Endpoint = "http://x", Model = "m", Temperature = null }).CompleteAsync("s", "u", default);
        Assert.DoesNotContain("temperature", h.Requests[0].Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"rate"}""")]
    [InlineData(HttpStatusCode.OK, """{"choices":[]}""")]
    [InlineData(HttpStatusCode.OK, """<html>""")]
    [InlineData(HttpStatusCode.OK, """{"choices":[{"message":{"content":""}}]}""")]
    public async Task Provider_failures_become_execution_errors(HttpStatusCode status, string body)
    {
        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() =>
            Make(new StubHandler((_, _) => StubHandler.Json(body, status))).CompleteAsync("s", "u", default));
        Assert.DoesNotContain("sk-secret", e.Message);
    }

    [Fact]
    public async Task Network_error_becomes_execution_error() =>
        await Assert.ThrowsAsync<SmartWizzException>(() =>
            Make(new StubHandler((_, _) => throw new HttpRequestException("refused"))).CompleteAsync("s", "u", default));

    [Fact]
    public async Task Missing_endpoint_is_reported() =>
        await Assert.ThrowsAsync<SmartWizzException>(() =>
            Make(new StubHandler((_, _) => StubHandler.Chat("x")), new ProviderConfig()).CompleteAsync("s", "u", default));
}

public class AzureDevOpsTests
{
    static readonly AzureDevOpsConfig Cfg = new()
    {
        CollectionUri = "https://tfs.local/tfs/Coll/",
        Project = "My Proj",
        RepositoryId = "repo",
        PullRequestId = 7,
    };

    static Finding F(string fp, int? end = null) => new("null-deref", Severity.High, "Title", "Msg", "src/a.cs", 10, end, 0.8, fp);

    [Fact]
    public void Thread_payload_targets_right_side_lines()
    {
        string json = JsonSerializer.Serialize(AzureDevOpsPublisher.ThreadPayload(F("abc", 12)));
        using var doc = JsonDocument.Parse(json);
        JsonElement ctx = doc.RootElement.GetProperty("threadContext");
        Assert.Equal("/src/a.cs", ctx.GetProperty("filePath").GetString());
        Assert.Equal(10, ctx.GetProperty("rightFileStart").GetProperty("line").GetInt32());
        Assert.Equal(12, ctx.GetProperty("rightFileEnd").GetProperty("line").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Contains("SmartWizz fingerprint: abc", doc.RootElement.GetProperty("comments")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Posts_only_findings_not_already_on_the_pr()
    {
        string existing = JsonSerializer.Serialize(new
        {
            value = new object[]
            {
                new { comments = new[] { new { content = PrComment.Text(F("00000000000000aa")) } } },
                new { comments = Array.Empty<object>() },
            },
        });
        var h = new StubHandler((req, _) => req.Method == HttpMethod.Get ? StubHandler.Json(existing) : StubHandler.Json("{}"));

        (int posted, int present) = await new AzureDevOpsPublisher(new HttpClient(h), Cfg, "tok").PublishAsync([F("00000000000000aa"), F("00000000000000bb")], default);

        Assert.Equal((1, 1), (posted, present));
        (HttpRequestMessage Request, string? Body) post = h.Requests.Single(r => r.Request.Method == HttpMethod.Post);
        Assert.Equal("https://tfs.local/tfs/Coll/My%20Proj/_apis/git/repositories/repo/pullRequests/7/threads?api-version=6.0",
            post.Request.RequestUri!.AbsoluteUri);
        Assert.Equal("Basic " + Convert.ToBase64String("tok"u8.ToArray().Prepend((byte)':').ToArray()),
            post.Request.Headers.Authorization!.ToString());
        Assert.Contains("SmartWizz fingerprint: 00000000000000bb", post.Body);
    }

    [Fact]
    public async Task Publish_failure_is_an_execution_error()
    {
        var h = new StubHandler((_, _) => StubHandler.Json("denied", HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<SmartWizzException>(() =>
            new AzureDevOpsPublisher(new HttpClient(h), Cfg, "tok").PublishAsync([F("x")], default));
    }

    [Fact]
    public async Task Missing_settings_or_token_fail_before_any_request()
    {
        var h = new StubHandler((_, _) => StubHandler.Json("{}"));
        await Assert.ThrowsAsync<SmartWizzException>(() =>
            new AzureDevOpsPublisher(new HttpClient(h), new AzureDevOpsConfig(), "tok").PublishAsync([], default));
        await Assert.ThrowsAsync<SmartWizzException>(() =>
            new AzureDevOpsPublisher(new HttpClient(h), Cfg, null).PublishAsync([], default));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void Config_falls_back_to_pipeline_variables()
    {
        var env = new Dictionary<string, string>
        {
            ["SYSTEM_COLLECTIONURI"] = "https://tfs/",
            ["SYSTEM_TEAMPROJECT"] = "P",
            ["BUILD_REPOSITORY_ID"] = "R",
            ["SYSTEM_PULLREQUEST_PULLREQUESTID"] = "42",
        };
        string path = Path.GetTempFileName();
        File.WriteAllText(path, """{ "azureDevOps": { "project": "FromFile" }, "provider": { "model": "m" } }""");
        try
        {
            AzureDevOpsConfig az = Config.Load(path, k => env.GetValueOrDefault(k)).AzureDevOps;
            Assert.Equal(("https://tfs/", "FromFile", "R", 42), (az.CollectionUri, az.Project, az.RepositoryId, az.PullRequestId!.Value));
        }
        finally { File.Delete(path); }
    }
}

public class DotEnvTests
{
    [Fact]
    public void Loads_values_without_overriding_existing_environment()
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, "# comment\nSW_TEST_NEW=\"abc=1\"\nSW_TEST_SET=fromfile\nnot a pair\n");
        Environment.SetEnvironmentVariable("SW_TEST_SET", "fromenv");
        try
        {
            Config.LoadDotEnv(path);
            Assert.Equal("abc=1", Environment.GetEnvironmentVariable("SW_TEST_NEW"));
            Assert.Equal("fromenv", Environment.GetEnvironmentVariable("SW_TEST_SET"));
        }
        finally
        {
            File.Delete(path);
            Environment.SetEnvironmentVariable("SW_TEST_NEW", null);
            Environment.SetEnvironmentVariable("SW_TEST_SET", null);
        }
    }
}

public class GuidelinesTests
{
    [Fact]
    public void System_prompt_appends_standard_only_when_configured()
    {
        Assert.Equal("base", Review.SystemPrompt("base", null));
        string p = Review.SystemPrompt("base", "  - No magic numbers.\n");
        Assert.StartsWith("base", p);
        Assert.Contains("<standard>\n- No magic numbers.\n</standard>", p);
        Assert.Contains("standard-<short-kebab-rule-name>", p);
    }

    [Fact]
    public void Guidelines_path_resolves_relative_to_config_and_must_exist()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string cfgPath = Path.Combine(dir, "smartwizz.json");
            File.WriteAllText(cfgPath, """{ "review": { "guidelines": "ENGINEERING_STANDARD.md" } }""");
            Assert.Throws<SmartWizzException>(() => Config.Load(cfgPath, _ => null));

            File.WriteAllText(Path.Combine(dir, "ENGINEERING_STANDARD.md"), "rules");
            Assert.Equal(Path.Combine(dir, "ENGINEERING_STANDARD.md"), Config.Load(cfgPath, _ => null).Review.Guidelines);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ProviderDisposalTests
{
    sealed class FailingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new HttpRequestException("connection dropped");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    sealed class TrackedResponse : HttpResponseMessage
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task Response_is_disposed_when_reading_body_fails()
    {
        // A dropped body read is retried, so every attempt gets (and must dispose) its own response.
        var responses = new List<TrackedResponse>();
        var provider = new Provider(new HttpClient(new StubHandler((_, _) =>
            {
                var response = new TrackedResponse { Content = new FailingContent() };
                responses.Add(response);
                return response;
            })),
            new ProviderConfig { Endpoint = "http://x", Model = "m" }, null, delay: StubHandler.NoDelay);

        await Assert.ThrowsAsync<SmartWizzException>(() => provider.CompleteAsync("s", "u", default));
        Assert.Equal(3, responses.Count);
        Assert.All(responses, r => Assert.True(r.Disposed));
    }
}

public class ReportTests
{
    static ReviewResult Sample()
    {
        var r = new ReviewResult
        {
            ReviewedFiles = 2,
            Findings = [new Finding("sql-injection", Severity.Critical, "Raw SQL", "Line one\nLine two", "db.cs", 4, 6, 0.9, "fp1")],
        };
        r.SkippedFiles.Add(new SkippedFile("a.lock", "excluded"));
        r.Rejected.Add("x.cs:1: line not in diff");
        return r;
    }

    [Fact]
    public void Json_uses_camel_case_lowercase_severity_and_readable_text()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Report.ToJson(Sample()));
        JsonElement f = doc.RootElement.GetProperty("findings")[0];
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("critical", f.GetProperty("severity").GetString());
        Assert.Equal(6, f.GetProperty("endLine").GetInt32());
        Assert.Equal("excluded", doc.RootElement.GetProperty("skippedFiles")[0].GetProperty("reason").GetString());
        Assert.Contains("a + b", Report.ToJson(new ReviewResult { Findings = [new Finding("r", Severity.Low, "a + b", "m", "f", 1, null, 1, "x")] }));
    }

    [Fact]
    public void Console_lists_findings_with_location_and_indented_message()
    {
        var w = new StringWriter();
        Report.WriteConsole(Sample(), w);
        string text = w.ToString();
        Assert.Contains("1 finding(s) in 2 reviewed file(s); 1 skipped file(s), 1 rejected finding(s)", text);
        Assert.Contains("[CRITICAL] db.cs:4-6  Raw SQL", text);
        Assert.Contains("    Line two", text);
        Assert.Contains("Skipped: a.lock (excluded)", text);
    }
}

public class InternalProviderTests
{
    [Fact]
    public async Task Custom_key_header_replaces_bearer()
    {
        var h = new StubHandler((_, _) => StubHandler.Chat("ok"));
        var cfg = new ProviderConfig { Endpoint = "https://llm.internal/v1/chat/completions", Model = "m", ApiKeyHeader = "api-key" };
        await new Provider(new HttpClient(h), cfg, "k1").CompleteAsync("s", "u", default);

        HttpRequestMessage req = h.Requests.Single().Request;
        Assert.Null(req.Headers.Authorization);
        Assert.Equal("k1", Assert.Single(req.Headers.GetValues("api-key")));
    }

    [Fact]
    public async Task Invalid_key_header_is_a_config_error()
    {
        var h = new StubHandler((_, _) => StubHandler.Chat("ok"));
        var cfg = new ProviderConfig { Endpoint = "https://x", Model = "m", ApiKeyHeader = "bad header" };
        await Assert.ThrowsAsync<SmartWizzException>(() => new Provider(new HttpClient(h), cfg, "k").CompleteAsync("s", "u", default));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void Endpoint_and_model_env_vars_override_config()
    {
        var env = new Dictionary<string, string> { ["SMARTWIZZ_ENDPOINT"] = "https://llm.internal/v1/chat/completions", ["SMARTWIZZ_MODEL"] = "local-coder" };
        string path = Path.GetTempFileName();
        File.WriteAllText(path, """{ "provider": { "endpoint": "https://public", "model": "public-model" } }""");
        try
        {
            ProviderConfig p = Config.Load(path, k => env.GetValueOrDefault(k)).Provider;
            Assert.Equal(("https://llm.internal/v1/chat/completions", "local-coder"), (p.Endpoint, p.Model));
            env["SMARTWIZZ_ENDPOINT"] = "";
            Assert.Equal("https://public", Config.Load(path, k => env.GetValueOrDefault(k)).Provider.Endpoint);
        }
        finally { File.Delete(path); }
    }
}
