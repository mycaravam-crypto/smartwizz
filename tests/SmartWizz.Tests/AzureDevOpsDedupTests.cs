using System.Net;
using System.Text.Json;

namespace SmartWizz.Tests;

public class AzureDevOpsDedupTests
{
    static readonly AzureDevOpsConfig Cfg = new() { CollectionUri = "https://tfs/", Project = "P", RepositoryId = "R", PullRequestId = 1 };

    static Finding F(string fp) => new("r", Severity.High, "t", "m", "a.cs", 1, null, 0.9, fp);

    static string Threads(params string[][] commentsPerThread) => JsonSerializer.Serialize(new
    {
        value = commentsPerThread.Select(cs => new { comments = cs.Select(c => new { content = c }) }),
        count = commentsPerThread.Length,
    });

    static (StubHandler Handler, AzureDevOpsPublisher Publisher) Make(Func<HttpRequestMessage, HttpResponseMessage> threadsResponse,
        Func<int, HttpResponseMessage>? postResponse = null)
    {
        int posts = 0;
        var h = new StubHandler((req, _) => req.Method == HttpMethod.Get
            ? threadsResponse(req)
            : (postResponse ?? (_ => StubHandler.Json("{}")))(++posts));
        return (h, new AzureDevOpsPublisher(new HttpClient(h), Cfg, "tok"));
    }

    static string Fp(int i) => i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Looks_up_threads_once_then_posts_new_findings_in_order()
    {
        (StubHandler? h, AzureDevOpsPublisher? publisher) = Make(_ => StubHandler.Json(Threads()));

        Assert.Equal((2, 0), await publisher.PublishAsync([F(Fp(1)), F(Fp(2))], default));

        Assert.Equal(["GET", "POST", "POST"], h.Requests.Select(r => r.Request.Method.Method));
        Assert.Contains(Fp(1), h.Requests[1].Body);
        Assert.Contains(Fp(2), h.Requests[2].Body);
    }

    [Fact]
    public async Task Fingerprints_in_any_thread_or_reply_are_not_reposted()
    {
        string marker = AzureDevOpsPublisher.Marker(F(Fp(2)));
        (StubHandler? h, AzureDevOpsPublisher? publisher) = Make(_ => StubHandler.Json(Threads(
            ["human comment"],
            ["SmartWizz original", $"reply quoting it: {marker}"],
            [AzureDevOpsPublisher.CommentText(F(Fp(3)))])));

        Assert.Equal((1, 2), await publisher.PublishAsync([F(Fp(1)), F(Fp(2)), F(Fp(3))], default));
        Assert.Contains(Fp(1), h.Requests.Single(r => r.Request.Method == HttpMethod.Post).Body);
    }

    [Fact]
    public void Similar_but_different_fingerprints_do_not_match()
    {
        HashSet<string> found = AzureDevOpsPublisher.ParseFingerprints(Threads([$"SmartWizz fingerprint: {Fp(1)}0"]));
        Assert.Equal([Fp(1)], found); // 16 hex chars captured; a longer token does not create a new match
        Assert.DoesNotContain(Fp(2), found);
    }

    [Fact]
    public async Task Threads_without_comments_are_handled()
    {
        (StubHandler _, AzureDevOpsPublisher? publisher) = Make(_ => StubHandler.Json("""{"value":[{"id":1},{"id":2,"comments":[{"isDeleted":true}]}],"count":2}"""));
        Assert.Equal((1, 0), await publisher.PublishAsync([F(Fp(1))], default));
    }

    [Fact]
    public async Task Paginated_response_fails_closed()
    {
        (StubHandler? h, AzureDevOpsPublisher? publisher) = Make(_ =>
        {
            HttpResponseMessage res = StubHandler.Json(Threads());
            res.Headers.Add("x-ms-continuationtoken", "abc");
            return res;
        });

        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() => publisher.PublishAsync([F(Fp(1))], default));
        Assert.Equal(FailureKind.Publish, e.Kind);
        Assert.DoesNotContain(h.Requests, r => r.Request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("""{"value":[],"count":3}""")]            // count says more than received
    [InlineData("""{"count":0}""")]                       // no value
    [InlineData("""{"value":{},"count":0}""")]            // value not an array
    [InlineData("""{"value":[{"comments":{}}]}""")]       // comments not an array
    [InlineData("""<html>login</html>""")]                // proxy/login page
    [InlineData("""{"value":[],"count":"zero"}""")]       // count not a number
    public async Task Unexpected_threads_response_fails_closed(string body)
    {
        (StubHandler? h, AzureDevOpsPublisher? publisher) = Make(_ => StubHandler.Json(body));
        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() => publisher.PublishAsync([F(Fp(1))], default));
        Assert.Equal(FailureKind.Publish, e.Kind);
        Assert.Single(h.Requests); // the GET only
    }

    [Fact]
    public async Task Failed_lookup_never_becomes_an_empty_list()
    {
        (StubHandler? h, AzureDevOpsPublisher? publisher) = Make(_ => StubHandler.Json("oops", HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<SmartWizzException>(() => publisher.PublishAsync([F(Fp(1))], default));
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Post_failure_stops_publishing_and_keeps_earlier_threads_for_rerun_dedup()
    {
        (StubHandler? h, AzureDevOpsPublisher? publisher) = Make(_ => StubHandler.Json(Threads()),
            n => n == 2 ? StubHandler.Json("nope", HttpStatusCode.BadRequest) : StubHandler.Json("{}"));

        await Assert.ThrowsAsync<SmartWizzException>(() => publisher.PublishAsync([F(Fp(1)), F(Fp(2)), F(Fp(3))], default));
        Assert.Equal(["GET", "POST", "POST"], h.Requests.Select(r => r.Request.Method.Method));
    }

    [Fact]
    public async Task Empty_result_posts_everything() =>
        Assert.Equal((1, 0), await Make(_ => StubHandler.Json("""{"value":[],"count":0}""")).Publisher.PublishAsync([F(Fp(1))], default));
}
