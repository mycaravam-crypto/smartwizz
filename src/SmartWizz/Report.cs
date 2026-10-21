using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartWizz;

public static class Report
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // file output, not HTML
    };

    public static string ToJson(ReviewResult r, ReviewPlan? plan = null, ReviewCheckpoint? checkpoint = null) => JsonSerializer.Serialize(new
    {
        version = 1,
        mode = plan?.Mode.ToString().ToLowerInvariant(),
        modeReason = plan?.Reason,
        r.ReviewedFiles,
        r.Models,
        r.RulesetVersion,
        r.RulesetSha256,
        r.SystemPromptCharacters,
        r.UserPromptCharacters,
        r.PromptTokens,
        r.CompletionTokens,
        r.Findings,
        r.SkippedFiles,
        r.Rejected,
        trackedFindings = checkpoint?.Findings,
    }, Json);

    // Lifecycle of findings across runs; this run's new findings are already listed by WriteConsole.
    public static void WriteCheckpoint(ReviewCheckpoint next, ReviewCheckpoint? previous, TextWriter w)
    {
        var wasResolved = (previous?.Findings ?? []).Where(t => !t.IsUnresolved)
            .Select(t => t.Finding.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var resolvedNow = next.Findings
            .Where(t => !t.IsUnresolved && t.LastCheckedSha == next.HeadSha && !wasResolved.Contains(t.Finding.Fingerprint)).ToList();
        var carried = next.Findings.Where(t => t.IsUnresolved && t.FirstSeenSha != next.HeadSha).ToList();
        int regressed = next.Findings.Count(t => t.Status == FindingStatus.Regressed);

        w.WriteLine();
        w.WriteLine($"Tracked findings: {next.UnresolvedFindings.Count} unresolved ({carried.Count} from earlier reviews, " +
                    $"{regressed} regressed), {resolvedNow.Count} resolved by this change.");
        foreach (TrackedFinding t in carried)
            w.WriteLine($"  [{Label(t)}] {t.Finding.FilePath}:{t.Finding.StartLine}  {t.Finding.Title}  (fp {t.Finding.Fingerprint})");
        foreach (TrackedFinding t in resolvedNow)
            w.WriteLine($"  [RESOLVED] {t.Finding.FilePath}:{t.Finding.StartLine}  {t.Finding.Title}: {t.Resolution}  (fp {t.Finding.Fingerprint})");
    }

    static string Label(TrackedFinding t) =>
        $"{t.Finding.Severity.ToString().ToUpperInvariant()}{(t.Status == FindingStatus.Regressed ? ", REGRESSED" : "")}";

    public static void WriteConsole(ReviewResult r, TextWriter w)
    {
        w.WriteLine($"SmartWizz: {r.Findings.Count} finding(s) in {r.ReviewedFiles} reviewed file(s); " +
                    $"{r.SkippedFiles.Count} skipped file(s), {r.Rejected.Count} rejected finding(s).");
        if (r.Models.Count > 0)
            w.WriteLine($"Reviewed by: {string.Join(", ", r.Models)}");
        foreach (Finding f in r.Findings)
        {
            string lines = f.EndLine is { } e ? $"{f.StartLine}-{e}" : $"{f.StartLine}";
            w.WriteLine();
            w.WriteLine($"[{f.Severity.ToString().ToUpperInvariant()}] {f.FilePath}:{lines}  {f.Title}");
            foreach (string line in f.Message.Split('\n'))
                w.WriteLine($"    {line.TrimEnd()}");
            w.WriteLine($"    ({f.RuleId}, confidence {f.Confidence:0.00}, fp {f.Fingerprint})");
        }
        if (r.SkippedFiles.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("Skipped: " + string.Join(", ", r.SkippedFiles.Select(s => $"{s.Path} ({s.Reason})")));
        }
        if (r.Rejected.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("Rejected: " + string.Join("; ", r.Rejected));
        }
    }
}
