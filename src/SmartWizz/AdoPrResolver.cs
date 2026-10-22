using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SmartWizz;

// Read-only handoff: ado-devops resolves a PR and fetches its exact merge-base comparison.
// SmartWizz uses the returned commit IDs rather than the checkout's potentially unrelated HEAD.
public static class AdoPrResolver
{
    public sealed record Resolved(string Base, string Head, string RepositoryId, string TargetBranch);

    public static Resolved ParseContext(string json, int requestedId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var pr = doc.RootElement.GetProperty("pullRequest");
            int id = pr.GetProperty("pullRequestId").GetInt32();
            if (id != requestedId) throw new SmartWizzException(FailureKind.Git, "ado returned a different pull request");
            string source = pr.GetProperty("lastMergeSourceCommit").GetProperty("commitId").GetString() ?? "";
            string target = pr.GetProperty("lastMergeTargetCommit").GetProperty("commitId").GetString() ?? "";
            string repo = pr.GetProperty("repository").GetProperty("id").GetString() ?? "";
            string targetBranch = pr.GetProperty("targetRefName").GetString() ?? "";
            if (!ReviewCheckpoint.IsCommitSha(source) || !ReviewCheckpoint.IsCommitSha(target)
                || !Guid.TryParse(repo, out _) || !targetBranch.StartsWith("refs/heads/", StringComparison.Ordinal))
                throw new SmartWizzException(FailureKind.Git, "ado returned incomplete PR commit or repository metadata");
            return new(target, source, repo, targetBranch);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SmartWizzException(FailureKind.Git, "invalid ado pr context JSON", e);
        }
    }

    public static async Task<Resolved> ResolveAsync(string checkout, int pr, CancellationToken ct)
    {
        string context = await RunAsync(checkout, ["pr", "context", pr.ToString(System.Globalization.CultureInfo.InvariantCulture)], ct);
        var resolved = ParseContext(context, pr);
        // The ado diff command verifies the local repository and fetches both PR branches.
        // Its output is not printed; SmartWizz computes its own diff with the exact commit IDs.
        await RunAsync(checkout, ["pr", "diff", pr.ToString(System.Globalization.CultureInfo.InvariantCulture)], ct);
        return resolved;
    }

    static async Task<string> RunAsync(string checkout, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ado")
        {
            WorkingDirectory = Path.GetFullPath(checkout),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new SmartWizzException(FailureKind.Git, "failed to start ado-devops CLI");
        using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        Task<string> error = process.StandardError.ReadToEndAsync(ct);
        string output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new SmartWizzException(FailureKind.Git, $"ado pr {args[1]} failed ({process.ExitCode}): {(await error).Trim()}");
        return output;
    }
}
