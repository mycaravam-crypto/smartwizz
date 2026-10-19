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

    public static string ToJson(ReviewResult r) => JsonSerializer.Serialize(new
    {
        version = 1,
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
    }, Json);

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
