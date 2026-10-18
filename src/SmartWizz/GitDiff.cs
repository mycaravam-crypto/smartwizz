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
        foreach (var a in new[]
                 {
                     "-C", repo, "-c", "core.quotePath=false", "diff", "--no-color", "--no-ext-diff", "--no-textconv",
                     "--find-renames", $"-U{contextLines}", $"{baseRef}...{headRef}", "--",
                 })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new SmartWizzException("failed to start git");
        // Both streams drained concurrently so a full stderr pipe cannot block git.
        Task<string> stderr = p.StandardError.ReadToEndAsync(ct);
        string stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new SmartWizzException($"git diff failed ({p.ExitCode}): {(await stderr).Trim()}");
        return stdout;
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    public static List<FileDiff> Parse(string diff)
    {
        var files = new List<FileDiff>();
        FileDiff? cur = null;
        int newLine = 0;
        bool inHunk = false;

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git "))
            {
                // Path is fixed up by the "+++" line; this fallback covers binary/mode-only entries.
                var idx = line.LastIndexOf(" b/", StringComparison.Ordinal);
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
                    var p = Unquote(line[4..]);
                    if (p == "/dev/null") cur.IsDeleted = true;
                    else cur.Path = p.StartsWith("b/") ? p[2..] : p;
                }
                else if (line.StartsWith("deleted file mode")) cur.IsDeleted = true;
                else if (line.StartsWith("Binary files ")) cur.IsBinary = true;
            }

            var m = HunkHeader().Match(line);
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
        var name = System.IO.Path.GetFileName(path);
        return patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, path, ignoreCase: true)
                                 || FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));
    }

    // Only surrounding quotes are removed: with core.quotePath=false git quotes just paths containing
    // control characters or quotes, whose C-escapes are rare enough to leave undecoded.
    static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
}
