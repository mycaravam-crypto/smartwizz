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
            throw new SmartWizzException("azureDevOps.collectionUri, project, repositoryId and pullRequestId are required to publish");
        if (string.IsNullOrEmpty(token))
            throw new SmartWizzException("set SMARTWIZZ_AZDO_TOKEN or SYSTEM_ACCESSTOKEN to publish");

        var url = $"{cfg.CollectionUri.TrimEnd('/')}/{Uri.EscapeDataString(cfg.Project)}/_apis/git/repositories/" +
                  $"{Uri.EscapeDataString(cfg.RepositoryId)}/pullRequests/{cfg.PullRequestId}/threads?api-version={cfg.ApiVersion}";

        var existing = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        var existingText = ExistingCommentText(existing);

        int posted = 0, present = 0;
        foreach (var f in findings)
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
            try
            {
                using var res = await http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                    throw new SmartWizzException($"Azure DevOps {req.Method} returned {(int)res.StatusCode}: {(body.Length > 300 ? body[..300] + "..." : body)}");
                return body;
            }
            catch (HttpRequestException e)
            {
                throw new SmartWizzException($"Azure DevOps request failed: {e.Message}", e);
            }
        }
    }

    static string ExistingCommentText(string threadsJson)
    {
        var sb = new StringBuilder();
        try
        {
            using var doc = JsonDocument.Parse(threadsJson);
            foreach (var t in doc.RootElement.GetProperty("value").EnumerateArray())
                if (t.TryGetProperty("comments", out var comments))
                    foreach (var c in comments.EnumerateArray())
                        if (c.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                            sb.Append(content.GetString()).Append('\n');
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SmartWizzException("Azure DevOps returned an unexpected threads response", e);
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
