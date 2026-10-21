using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartWizz;

public sealed class FileDiff
{
    public required string Path { get; set; }
    public bool IsBinary { get; set; }
    public bool IsDeleted { get; set; }
    // New-side line number -> line text, for every line visible in the diff (context + added).
    public Dictionary<int, string> NewLines { get; } = [];
    // New-side line numbers this change added. Findings must start on one of these.
    public HashSet<int> AddedLines { get; } = [];
    // Diff body with new-side line numbers, as sent to the model.
    public StringBuilder Annotated { get; } = new();
}

public static partial class GitDiff
{
    public static async Task<string> RunAsync(string repo, string baseRef, string headRef, int contextLines, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        // --no-ext-diff / --no-textconv: never let repository config run commands.
        foreach (string? a in new[]
                 {
                     "-C", repo, "-c", "core.quotePath=false", "diff", "--no-color", "--no-ext-diff", "--no-textconv",
                     "--find-renames", $"-U{contextLines}", $"{baseRef}...{headRef}", "--",
                 })
            psi.ArgumentList.Add(a);

        using Process p = Process.Start(psi) ?? throw new SmartWizzException(FailureKind.Git, "failed to start git");
        // Cancelling the reads alone would leave git running.
        using CancellationTokenRegistration kill = ct.Register(() => KillQuietly(p));
        // Both streams drained concurrently so a full stderr pipe cannot block git.
        Task<string> stderr = p.StandardError.ReadToEndAsync(ct);
        string stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        ct.ThrowIfCancellationRequested(); // a killed git must surface as cancellation, not a git error
        if (p.ExitCode != 0)
            throw new SmartWizzException(FailureKind.Git, $"git diff failed ({p.ExitCode}): {(await stderr).Trim()}");
        return stdout;
    }

    static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited: nothing to stop.
        }
    }

    // Incremental reviews require an ancestor baseline. A rewritten history falls back to the full PR diff.
    public static async Task<string> RunIncrementalAsync(string repo, string baseRef, string headRef,
        string? sinceSha, int contextLines, CancellationToken ct)
    {
        if (sinceSha is null)
            return await RunAsync(repo, baseRef, headRef, contextLines, ct);
        if (!System.Text.RegularExpressions.Regex.IsMatch(sinceSha, "^[0-9a-fA-F]{40}$"))
            throw new SmartWizzException(FailureKind.Usage, "--since must be a full 40-character commit SHA");

        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in new[] { "-C", repo, "merge-base", "--is-ancestor", sinceSha, headRef })
            psi.ArgumentList.Add(a);
        using Process process = Process.Start(psi) ?? throw new SmartWizzException(FailureKind.Git, "failed to start git");
        using CancellationTokenRegistration kill = ct.Register(() => KillQuietly(process));
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode == 1)
            return await RunAsync(repo, baseRef, headRef, contextLines, ct);
        if (process.ExitCode != 0)
            throw new SmartWizzException(FailureKind.Git, $"git merge-base failed ({process.ExitCode}): {stderr.Trim()}");
        return await RunAsync(repo, sinceSha, headRef, contextLines, ct);
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    public static List<FileDiff> Parse(string diff)
    {
        var files = new List<FileDiff>();
        FileDiff? cur = null;
        int newLine = 0;
        bool inHunk = false;

        foreach (string line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git "))
            {
                // Path is fixed up by the "+++" line; this fallback covers binary/mode-only entries.
                int idx = line.LastIndexOf(" b/", StringComparison.Ordinal);
                cur = new FileDiff { Path = Unquote(idx >= 0 ? line[(idx + 3)..] : line) };
                files.Add(cur);
                inHunk = false;
                continue;
            }
            if (cur is null) continue;

            if (!inHunk)
            {
                if (line.StartsWith("+++ "))
                {
                    string p = Unquote(line[4..]);
                    if (p == "/dev/null") cur.IsDeleted = true;
                    else cur.Path = p.StartsWith("b/") ? p[2..] : p;
                }
                else if (line.StartsWith("rename to ")) cur.Path = Unquote(line["rename to ".Length..]);
                else if (line.StartsWith("deleted file mode")) cur.IsDeleted = true;
                else if (line.StartsWith("Binary files ")) cur.IsBinary = true;
            }

            Match m = HunkHeader().Match(line);
            if (m.Success)
            {
                newLine = int.Parse(m.Groups[1].Value);
                inHunk = true;
                cur.Annotated.Append(line).Append('\n');
                continue;
            }
            if (!inHunk || line.Length == 0) continue;

            switch (line[0])
            {
                case ' ':
                case '+':
                    if (line[0] == '+') cur.AddedLines.Add(newLine);
                    cur.NewLines[newLine] = line[1..];
                    cur.Annotated.Append($"{newLine,6} |{line}\n");
                    newLine++;
                    break;
                case '-':
                    cur.Annotated.Append($"{"",6} |{line}\n");
                    break;
                // '\' = "\ No newline at end of file"; anything else ends the hunk.
                case '\\': break;
                default: inHunk = false; break;
            }
        }
        return files;
    }

    public static bool IsExcluded(string path, IEnumerable<string> patterns)
    {
        string name = System.IO.Path.GetFileName(path);
        return patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, path, ignoreCase: true)
                                 || FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));
    }

    // Only surrounding quotes are removed: with core.quotePath=false git quotes just paths containing
    // control characters or quotes, whose C-escapes are rare enough to leave undecoded.
    static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
}
