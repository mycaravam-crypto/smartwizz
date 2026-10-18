using System.Net;
using System.Text.Json;

namespace SmartWizz.Tests;

public class GitHubTests
{
    const string Sha = "0123456789abcdef0123456789abcdef01234567";
    const string CommentsUrl = "https://api.github.com/repos/o/r/pulls/7/comments?per_page=100";

    static readonly GitHubConfig Cfg = new() { Repository = "o/r", PullRequest = 7, CommitId = Sha };

    static Finding F(string fp, string message = "m", int line = 12) =>
        new("null-deref", Severity.High, "Title", message, "src/a.rs", line, null, 0.9, fp);

    static string Fp(int i) => i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    static string Comments(params string[] bodies) => JsonSerializer.Serialize(bodies.Select(b => new { body = b }));

    static HttpResponseMessage Page(string json, string? next = null)
    {
        HttpResponseMessage res = StubHandler.Json(json);
        if (next is not null) res.Headers.Add("Link", $"<{next}>; rel=\"next\", <{next}>; rel=\"last\"");
        return res;
    }

    static (StubHandler, GitHubPublisher) Make(Func<HttpRequestMessage, HttpResponseMessage> respond, GitHubConfig? cfg = null)
    {
        var h = new StubHandler((req, _) => respond(req));
        return (h, new GitHubPublisher(new HttpClient(h), cfg ?? Cfg, "ghs_token"));
    }

    [Fact]
    public async Task Posts_one_comment_review_with_inline_comments_after_reading_existing_ones()
    {
        (StubHandler? h, GitHubPublisher? publisher) = Make(req => req.Method == HttpMethod.Get ? Page("[]") : StubHandler.Json("{}"));

        Assert.Equal((2, 0), await publisher.PublishAsync([F(Fp(1)), F(Fp(2), line: 30)], default));

        Assert.Equal(["GET", "POST"], h.Requests.Select(r => r.Request.Method.Method));
        Assert.Equal(CommentsUrl, h.Requests[0].Request.RequestUri!.AbsoluteUri);
        Assert.Equal("https://api.github.com/repos/o/r/pulls/7/reviews", h.Requests[1].Request.RequestUri!.AbsoluteUri);
        HttpRequestMessage post = h.Requests[1].Request;
        Assert.Equal("Bearer ghs_token", post.Headers.Authorization!.ToString());
        Assert.Contains("smartwizz", post.Headers.UserAgent.ToString());
        Assert.Equal("2022-11-28", post.Headers.GetValues("X-GitHub-Api-Version").Single());

        using var review = JsonDocument.Parse(h.Requests[1].Body!);
        Assert.Equal(Sha, review.RootElement.GetProperty("commit_id").GetString());
        Assert.Equal("COMMENT", review.RootElement.GetProperty("event").GetString()); // never APPROVE / REQUEST_CHANGES
        JsonElement[] comments = [.. review.RootElement.GetProperty("comments").EnumerateArray()];
        Assert.Equal(2, comments.Length);
        Assert.Equal("src/a.rs", comments[0].GetProperty("path").GetString());
        Assert.Equal(12, comments[0].GetProperty("line").GetInt32());
        Assert.Equal("RIGHT", comments[0].GetProperty("side").GetString());
        Assert.Contains($"SmartWizz fingerprint: {Fp(1)}", comments[0].GetProperty("body").GetString());
        Assert.Equal(30, comments[1].GetProperty("line").GetInt32());
    }

    [Fact]
    public void Model_text_cannot_mention_users()
    {
        string json = JsonSerializer.Serialize(GitHubPublisher.ReviewPayload([F(Fp(1), message: "ask @octocat or @org/team")], Sha));
        Assert.DoesNotContain("@octocat", json);
        Assert.DoesNotContain("@org/team", json);
        Assert.Contains("@\\u2060octocat", json);
    }

    [Fact]
    public async Task Follows_pagination_and_skips_findings_already_commented_on_any_page()
    {
        const string page2 = "https://api.github.com/repositories/1/pulls/7/comments?per_page=100&page=2";
        (StubHandler? h, GitHubPublisher? publisher) = Make(req => req.Method == HttpMethod.Post
            ? StubHandler.Json("{}")
            : req.RequestUri!.AbsoluteUri == page2
                ? Page(Comments(PrComment.Text(F(Fp(2)))))
                : Page(Comments("a human comment"), next: page2));

        Assert.Equal((1, 1), await publisher.PublishAsync([F(Fp(1)), F(Fp(2))], default));

        Assert.Equal(["GET", "GET", "POST"], h.Requests.Select(r => r.Request.Method.Method));
        Assert.Contains(Fp(1), h.Requests[2].Body);
        Assert.DoesNotContain(Fp(2), h.Requests[2].Body);
    }

    [Fact]
    public async Task Nothing_new_posts_nothing()
    {
        (StubHandler? h, GitHubPublisher? publisher) = Make(_ => Page(Comments(PrComment.Text(F(Fp(1))))));
        Assert.Equal((0, 1), await publisher.PublishAsync([F(Fp(1))], default));
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Token_never_follows_pagination_to_another_host()
    {
        (StubHandler? h, GitHubPublisher? publisher) = Make(_ => Page("[]", next: "https://evil.example/collect"));
        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() => publisher.PublishAsync([F(Fp(1))], default));
        Assert.Equal(FailureKind.Publish, e.Kind);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Endless_pagination_fails_closed()
    {
        (StubHandler? h, GitHubPublisher? publisher) = Make(_ => Page("[]", next: CommentsUrl));
        await Assert.ThrowsAsync<SmartWizzException>(() => publisher.PublishAsync([F(Fp(1))], default));
        Assert.DoesNotContain(h.Requests, r => r.Request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("""{"message":"Not Found"}""", HttpStatusCode.OK)]       // object, not the documented array
    [InlineData("""<html>""", HttpStatusCode.OK)]
    [InlineData("""{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized)]
    public async Task Unreadable_existing_comments_fail_closed(string body, HttpStatusCode status)
    {
        (StubHandler? h, GitHubPublisher? publisher) = Make(_ => StubHandler.Json(body, status));
        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() => publisher.PublishAsync([F(Fp(1))], default));
        Assert.Equal(FailureKind.Publish, e.Kind);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Rejected_review_is_a_publish_error()
    {
        (StubHandler _, GitHubPublisher? publisher) = Make(req => req.Method == HttpMethod.Get
            ? Page("[]")
            : StubHandler.Json("""{"message":"Unprocessable Entity"}""", HttpStatusCode.UnprocessableEntity));
        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() => publisher.PublishAsync([F(Fp(1))], default));
        Assert.Contains("422", e.Message);
    }

    [Theory]
    [InlineData(null, 7, Sha)]
    [InlineData("not-a-repo", 7, Sha)]
    [InlineData("o/r", null, Sha)]
    [InlineData("o/r", 7, "HEAD")]
    public async Task Missing_or_invalid_settings_fail_before_any_request(string? repo, int? pr, string sha)
    {
        (StubHandler? h, GitHubPublisher? publisher) = Make(_ => Page("[]"), new GitHubConfig { Repository = repo, PullRequest = pr, CommitId = sha });
        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() => publisher.PublishAsync([F(Fp(1))], default));
        Assert.Equal(FailureKind.Configuration, e.Kind);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Missing_token_fails_before_any_request()
    {
        var h = new StubHandler((_, _) => Page("[]"));
        await Assert.ThrowsAsync<SmartWizzException>(() => new GitHubPublisher(new HttpClient(h), Cfg, null).PublishAsync([F(Fp(1))], default));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void Config_comes_from_the_actions_environment()
    {
        var env = new Dictionary<string, string>
        {
            ["GITHUB_REPOSITORY"] = "o/r",
            ["GITHUB_API_URL"] = "https://ghe.example/api/v3",
            ["SMARTWIZZ_GITHUB_PR"] = "45",
            ["SMARTWIZZ_GITHUB_SHA"] = Sha,
        };
        string path = Path.GetTempFileName();
        File.WriteAllText(path, "{}");
        try
        {
            GitHubConfig gh = Config.Load(path, k => env.GetValueOrDefault(k)).GitHub;
            Assert.Equal(("o/r", 45, Sha, "https://ghe.example/api/v3"), (gh.Repository, gh.PullRequest!.Value, gh.CommitId, gh.EffectiveApiUrl));
            Assert.Equal("https://api.github.com", new GitHubConfig().EffectiveApiUrl);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Options_accept_publish_github() =>
        Assert.True(Options.Parse(["--base", "main", "--publish-github"]).PublishGitHub);
}
