using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartWizz;

public sealed partial class AzureDevOpsPublisher(HttpClient http, AzureDevOpsConfig cfg, string? token)
{
    public async Task<(int Posted, int AlreadyPresent)> PublishAsync(IReadOnlyList<Finding> findings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.CollectionUri) || string.IsNullOrWhiteSpace(cfg.Project)
            || string.IsNullOrWhiteSpace(cfg.RepositoryId) || cfg.PullRequestId is null)
            throw new SmartWizzException(FailureKind.Configuration, "azureDevOps.collectionUri, project, repositoryId and pullRequestId are required to publish");
        if (string.IsNullOrEmpty(token))
            throw new SmartWizzException(FailureKind.Configuration, "set SMARTWIZZ_AZDO_TOKEN or SYSTEM_ACCESSTOKEN to publish");

        string url = $"{cfg.CollectionUri.TrimEnd('/')}/{Uri.EscapeDataString(cfg.Project)}/_apis/git/repositories/" +
                  $"{Uri.EscapeDataString(cfg.RepositoryId)}/pullRequests/{cfg.PullRequestId}/threads?api-version={cfg.ApiVersion}";

        // Must complete before any POST: without it, reruns would duplicate threads.
        HashSet<string> existing = await ExistingFingerprintsAsync(url, ct);

        int posted = 0, present = 0;
        foreach (Finding f in findings)
        {
            if (existing.Contains(f.Fingerprint))
            {
                present++;
                continue;
            }
            using HttpRequestMessage post = new(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(ThreadPayload(f)), Encoding.UTF8, "application/json"),
            };
            await SendAsync(post, ct);
            posted++;
        }
        return (posted, present);
    }

    async Task<HashSet<string>> ExistingFingerprintsAsync(string url, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        (string body, bool paginated) = await SendAsync(request, ct);
        // The threads List API documents no paging (no $top/$skip/continuation) in any version. If a server
        // paginates anyway, the lookup would be incomplete, so refuse rather than guess the protocol.
        if (paginated)
            throw new SmartWizzException(FailureKind.Publish,
                "Azure DevOps paginated the PR threads response, which SmartWizz cannot verify as complete; nothing was posted");
        return ParseFingerprints(body);
    }

    async Task<(string Body, bool Paginated)> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        // Basic with empty user works for both PATs and System.AccessToken.
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        try
        {
            using HttpResponseMessage res = await http.SendAsync(req, timeout.Token);
            string body = await res.Content.ReadAsStringAsync(timeout.Token);
            if (!res.IsSuccessStatusCode)
                throw new SmartWizzException(FailureKind.Publish, $"Azure DevOps {req.Method} returned {(int)res.StatusCode}: {(body.Length > 300 ? body[..300] + "..." : body)}");
            return (body, res.Headers.Contains("x-ms-continuationtoken"));
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new SmartWizzException(FailureKind.Timeout, $"Azure DevOps {req.Method} timed out after {cfg.TimeoutSeconds}s", e);
        }
        catch (HttpRequestException e)
        {
            throw new SmartWizzException(FailureKind.Publish, $"Azure DevOps request failed: {e.Message}", e);
        }
    }

    [GeneratedRegex(@"SmartWizz fingerprint: ([0-9a-f]{16})")]
    private static partial Regex FingerprintMarker();

    // Every fingerprint in any comment of any thread (including replies). A response that is not exactly the
    // documented shape fails the publish instead of being read as "no existing threads".
    public static HashSet<string> ParseFingerprints(string threadsJson)
    {
        HashSet<string> fingerprints = [];
        try
        {
            using var doc = JsonDocument.Parse(threadsJson);
            JsonElement threads = doc.RootElement.GetProperty("value");
            if (doc.RootElement.TryGetProperty("count", out JsonElement count) && count.GetInt32() != threads.GetArrayLength())
                throw new SmartWizzException(FailureKind.Publish,
                    $"Azure DevOps threads response is incomplete (count {count.GetInt32()}, received {threads.GetArrayLength()}); nothing was posted");

            foreach (JsonElement thread in threads.EnumerateArray())
            {
                if (!thread.TryGetProperty("comments", out JsonElement comments)) continue;
                foreach (JsonElement comment in comments.EnumerateArray())
                {
                    if (comment.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                    {
                        foreach (Match m in FingerprintMarker().Matches(content.GetString()!))
                            fingerprints.Add(m.Groups[1].Value);
                    }
                }
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new SmartWizzException(FailureKind.Publish, "Azure DevOps returned an unexpected threads response; nothing was posted", e);
        }
        return fingerprints;
    }

    // Visible on purpose: HTML comments are not reliably preserved across server versions.
    public static string Marker(Finding f) => $"SmartWizz fingerprint: {f.Fingerprint}";

    public static string CommentText(Finding f) =>
        $"**[{f.Severity}] {f.Title}**\n\n{f.Message}\n\n" +
        $"_`{f.RuleId}` · confidence {f.Confidence:0.00} · AI-generated review comment, verify before acting._\n\n" +
        $"<sub>{Marker(f)}</sub>";

    public static object ThreadPayload(Finding f) => new
    {
        comments = new[] { new { parentCommentId = 0, content = CommentText(f), commentType = 1 } },
        status = 1, // active
        threadContext = new
        {
            filePath = "/" + f.FilePath,
            rightFileStart = new { line = f.StartLine, offset = 1 },
            rightFileEnd = new { line = f.EndLine ?? f.StartLine, offset = 1 },
        },
    };
}
