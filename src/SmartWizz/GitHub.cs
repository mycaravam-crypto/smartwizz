using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartWizz;

// Posts all new findings as one GitHub pull request review (event COMMENT: never approves or requests changes),
// one inline comment per finding on its start line. Findings always start on an added line, which GitHub accepts
// as a review position; a multi-line range could reach beyond GitHub's own diff context and reject the whole review.
public sealed partial class GitHubPublisher(HttpClient http, GitHubConfig cfg, string? token)
{
    // 100 pages x 100 comments: far beyond any real PR, and a hard stop against a looping Link header.
    const int MaxPages = 100;

    public async Task<(int Posted, int AlreadyPresent)> PublishAsync(IReadOnlyList<Finding> findings, CancellationToken ct)
    {
        if (cfg.Repository is null || !RepositoryName().IsMatch(cfg.Repository) || cfg.PullRequest is null or <= 0
            || cfg.CommitId is null || !CommitSha().IsMatch(cfg.CommitId))
            throw new SmartWizzException(FailureKind.Configuration,
                "github.repository (owner/name), pullRequest and commitId (40-hex SHA) are required to publish; " +
                "in Actions: GITHUB_REPOSITORY, SMARTWIZZ_GITHUB_PR and SMARTWIZZ_GITHUB_SHA");
        if (string.IsNullOrEmpty(token))
            throw new SmartWizzException(FailureKind.Configuration, "set GITHUB_TOKEN or SMARTWIZZ_GITHUB_TOKEN to publish to GitHub");

        Uri api = new($"{cfg.EffectiveApiUrl.TrimEnd('/')}/repos/{cfg.Repository}/pulls/{cfg.PullRequest}");
        // Must complete before posting: without it, reruns would duplicate comments.
        HashSet<string> existing = await ExistingFingerprintsAsync(api, ct);

        var fresh = findings.Where(f => !existing.Contains(f.Fingerprint)).ToList();
        if (fresh.Count > 0)
        {
            using HttpRequestMessage post = new(HttpMethod.Post, $"{api}/reviews")
            {
                Content = new StringContent(JsonSerializer.Serialize(ReviewPayload(fresh, cfg.CommitId)), Encoding.UTF8, "application/json"),
            };
            await SendAsync(post, ct);
        }
        return (fresh.Count, findings.Count - fresh.Count);
    }

    public static object ReviewPayload(IReadOnlyList<Finding> findings, string commitId) => new
    {
        commit_id = commitId,
        @event = "COMMENT",
        body = NoMentions($"**SmartWizz** found {findings.Count} new issue(s) in this pull request. AI-generated review: verify before acting."),
        comments = findings.Select(f => new { path = f.FilePath, line = f.StartLine, side = "RIGHT", body = NoMentions(PrComment.Text(f)) }),
    };

    // Model text must not ping real users: a word joiner after "@" keeps it readable but breaks the mention.
    static string NoMentions(string text) => text.Replace("@", "@⁠", StringComparison.Ordinal);

    async Task<HashSet<string>> ExistingFingerprintsAsync(Uri api, CancellationToken ct)
    {
        HashSet<string> fingerprints = [];
        Uri? page = new($"{api}/comments?per_page=100");
        for (int pages = 0; page is not null; pages++)
        {
            if (pages == MaxPages)
                throw new SmartWizzException(FailureKind.Publish,
                    $"GitHub listed more than {MaxPages} pages of review comments; nothing was posted");
            using HttpRequestMessage get = new(HttpMethod.Get, page);
            (string body, string? link) = await SendAsync(get, ct);
            fingerprints.UnionWith(ParseFingerprints(body));
            page = NextPage(link, api);
        }
        return fingerprints;
    }

    // Fingerprints in the bodies of a review-comments page. Anything but a JSON array fails the publish instead of
    // being read as "no existing comments".
    public static HashSet<string> ParseFingerprints(string commentsJson)
    {
        HashSet<string> fingerprints = [];
        try
        {
            using var doc = JsonDocument.Parse(commentsJson);
            foreach (JsonElement comment in doc.RootElement.EnumerateArray())
            {
                if (comment.TryGetProperty("body", out JsonElement body) && body.ValueKind == JsonValueKind.String)
                    fingerprints.UnionWith(PrComment.Fingerprints(body.GetString()!));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new SmartWizzException(FailureKind.Publish, "GitHub returned an unexpected review comments response; nothing was posted", e);
        }
        return fingerprints;
    }

    [GeneratedRegex(@"<([^>]+)>\s*;\s*rel=""next""")]
    private static partial Regex NextLink();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"^[0-9a-f]{40}$")]
    private static partial Regex CommitSha();

    // GitHub's documented pagination (Link: <url>; rel="next"). The token is only ever sent to the API host.
    public static Uri? NextPage(string? linkHeader, Uri api)
    {
        Match m = NextLink().Match(linkHeader ?? "");
        if (!m.Success) return null;
        if (!Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out Uri? next) || next.Scheme != api.Scheme || next.Authority != api.Authority)
            throw new SmartWizzException(FailureKind.Publish, "GitHub pagination pointed to another host; nothing was posted");
        return next;
    }

    async Task<(string Body, string? Link)> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("smartwizz"); // GitHub rejects requests without a User-Agent
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        try
        {
            using HttpResponseMessage res = await http.SendAsync(req, timeout.Token);
            string body = await res.Content.ReadAsStringAsync(timeout.Token);
            if (!res.IsSuccessStatusCode)
                throw new SmartWizzException(FailureKind.Publish, $"GitHub {req.Method} returned {(int)res.StatusCode}: {(body.Length > 300 ? body[..300] + "..." : body)}");
            string? link = res.Headers.TryGetValues("Link", out IEnumerable<string>? values) ? string.Join(", ", values) : null;
            return (body, link);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new SmartWizzException(FailureKind.Timeout, $"GitHub {req.Method} timed out after {cfg.TimeoutSeconds}s", e);
        }
        catch (HttpRequestException e)
        {
            throw new SmartWizzException(FailureKind.Publish, $"GitHub request failed: {e.Message}", e);
        }
    }
}
