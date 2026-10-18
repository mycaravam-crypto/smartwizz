using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SmartWizz;

public sealed class AzureDevOpsPublisher(HttpClient http, AzureDevOpsConfig cfg, string? token)
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

        string existing = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        string existingText = ExistingCommentText(existing);

        int posted = 0, present = 0;
        foreach (Finding f in findings)
        {
            if (existingText.Contains(Marker(f), StringComparison.Ordinal))
            {
                present++;
                continue;
            }
            await SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(JsonSerializer.Serialize(ThreadPayload(f)), Encoding.UTF8, "application/json") }, ct);
            posted++;
        }
        return (posted, present);
    }

    async Task<string> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
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
                return body;
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
    }

    static string ExistingCommentText(string threadsJson)
    {
        var sb = new StringBuilder();
        try
        {
            using var doc = JsonDocument.Parse(threadsJson);
            foreach (JsonElement t in doc.RootElement.GetProperty("value").EnumerateArray())
                if (t.TryGetProperty("comments", out JsonElement comments))
                    foreach (JsonElement c in comments.EnumerateArray())
                        if (c.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                            sb.Append(content.GetString()).Append('\n');
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SmartWizzException(FailureKind.Publish, "Azure DevOps returned an unexpected threads response", e);
        }
        return sb.ToString();
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
