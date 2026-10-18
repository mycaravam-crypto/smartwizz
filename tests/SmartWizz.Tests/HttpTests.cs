using System.Net;
using System.Text.Json;
using SmartWizz;

public class ProviderTests
{
    static Provider Make(StubHandler h, ProviderConfig? cfg = null) =>
        new(new HttpClient(h), cfg ?? new ProviderConfig { Endpoint = "http://llm/v1/chat/completions", Model = "m" }, "sk-secret");

    [Fact]
    public async Task Sends_model_messages_and_bearer_key()
    {
        var h = new StubHandler((_, _) => StubHandler.Chat("ok"));
        Assert.Equal("ok", await Make(h).CompleteAsync("sys", "usr", default));

        var (req, body) = h.Requests.Single();
        Assert.Equal("Bearer sk-secret", req.Headers.Authorization!.ToString());
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("m", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal("usr", doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
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
        var e = await Assert.ThrowsAsync<SmartWizzException>(() =>
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
        CollectionUri = "https://tfs.local/tfs/Coll/", Project = "My Proj", RepositoryId = "repo", PullRequestId = 7,
    };

    static Finding F(string fp, int? end = null) => new("null-deref", Severity.High, "Title", "Msg", "src/a.cs", 10, end, 0.8, fp);

    [Fact]
    public void Thread_payload_targets_right_side_lines()
    {
        var json = JsonSerializer.Serialize(AzureDevOpsPublisher.ThreadPayload(F("abc", 12)));
        using var doc = JsonDocument.Parse(json);
        var ctx = doc.RootElement.GetProperty("threadContext");
        Assert.Equal("/src/a.cs", ctx.GetProperty("filePath").GetString());
        Assert.Equal(10, ctx.GetProperty("rightFileStart").GetProperty("line").GetInt32());
        Assert.Equal(12, ctx.GetProperty("rightFileEnd").GetProperty("line").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Contains("SmartWizz fingerprint: abc", doc.RootElement.GetProperty("comments")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Posts_only_findings_not_already_on_the_pr()
    {
        var existing = JsonSerializer.Serialize(new
        {
            value = new object[]
            {
                new { comments = new[] { new { content = AzureDevOpsPublisher.CommentText(F("old")) } } },
                new { comments = Array.Empty<object>() },
            },
        });
        var h = new StubHandler((req, _) => req.Method == HttpMethod.Get ? StubHandler.Json(existing) : StubHandler.Json("{}"));

        var (posted, present) = await new AzureDevOpsPublisher(new HttpClient(h), Cfg, "tok").PublishAsync([F("old"), F("new")], default);

        Assert.Equal((1, 1), (posted, present));
        var post = h.Requests.Single(r => r.Request.Method == HttpMethod.Post);
        Assert.Equal("https://tfs.local/tfs/Coll/My%20Proj/_apis/git/repositories/repo/pullRequests/7/threads?api-version=6.0",
            post.Request.RequestUri!.AbsoluteUri);
        Assert.Equal("Basic " + Convert.ToBase64String("tok"u8.ToArray().Prepend((byte)':').ToArray()),
            post.Request.Headers.Authorization!.ToString());
        Assert.Contains("SmartWizz fingerprint: new", post.Body);
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
            ["SYSTEM_COLLECTIONURI"] = "https://tfs/", ["SYSTEM_TEAMPROJECT"] = "P",
            ["BUILD_REPOSITORY_ID"] = "R", ["SYSTEM_PULLREQUEST_PULLREQUESTID"] = "42",
        };
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """{ "azureDevOps": { "project": "FromFile" }, "provider": { "model": "m" } }""");
        try
        {
            var az = Config.Load(path, k => env.GetValueOrDefault(k)).AzureDevOps;
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
        var path = Path.GetTempFileName();
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
        var p = Review.SystemPrompt("base", "  - No magic numbers.\n");
        Assert.StartsWith("base", p);
        Assert.Contains("<standard>\n- No magic numbers.\n</standard>", p);
        Assert.Contains("standard-<short-kebab-rule-name>", p);
    }

    [Fact]
    public void Guidelines_path_resolves_relative_to_config_and_must_exist()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cfgPath = Path.Combine(dir, "smartwizz.json");
            File.WriteAllText(cfgPath, """{ "review": { "guidelines": "ENGINEERING_STANDARD.md" } }""");
            Assert.Throws<SmartWizzException>(() => Config.Load(cfgPath, _ => null));

            File.WriteAllText(Path.Combine(dir, "ENGINEERING_STANDARD.md"), "rules");
            Assert.Equal(Path.Combine(dir, "ENGINEERING_STANDARD.md"), Config.Load(cfgPath, _ => null).Review.Guidelines);
        }
        finally { Directory.Delete(dir, true); }
    }
}
