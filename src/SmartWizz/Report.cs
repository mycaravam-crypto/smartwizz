using Spectre.Console;
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
        r.RulesetName,
        r.RulesetVersion,
        r.RulesetSha256,
        r.Requests,
        r.ReviewElapsedMilliseconds,
        r.RequestTimings,
        r.FindingRequests,
        r.SystemPromptCharacters,
        r.UserPromptCharacters,
        r.PromptTokens,
        r.CompletionTokens,
        r.Checks,
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

    static string Summary(ReviewResult r) =>
        $"SmartWizz: {r.Findings.Count} finding(s) in {r.ReviewedFiles} reviewed file(s); " +
        $"{r.SkippedFiles.Count} skipped file(s), {r.Rejected.Count} rejected finding(s).";

    static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // One line per finding, parsed by the problem matcher in .vscode/tasks.json.
    // Model text is flattened so it cannot forge a line for another file.
    public static void WriteProblems(ReviewResult r, TextWriter w)
    {
        w.WriteLine(Summary(r));
        foreach (Finding f in r.Findings)
        {
            string level = f.Severity switch { Severity.Critical or Severity.High => "error", Severity.Medium => "warning", _ => "info" };
            string fix = f.SuggestedFix is { } s ? $" Suggested fix: {OneLine(s)}" : "";
            w.WriteLine($"{f.FilePath}:{f.StartLine}:{level}: {OneLine(f.Title)} ({f.RuleId}): {OneLine(f.Message)}{fix} [fp {f.Fingerprint}]");
        }
    }

    // Treat model-generated text as data, never as Spectre markup or terminal commands.
    static string Safe(string value) => string.Concat(value.Where(c => !char.IsControl(c) || c == '\n' || c == '\t'));

    static string Duration(double milliseconds) =>
        FormattableString.Invariant($"{milliseconds / 1000:0.00}s");

    static string? FindingTiming(ReviewResult r, Finding f)
    {
        if (!r.FindingRequests.TryGetValue(f.Fingerprint, out int request)) return null;
        ReviewRequestTiming? timing = r.RequestTimings.FirstOrDefault(t => t.Request == request);
        return timing is null ? null : $"Request #{request}: {Duration(timing.ElapsedMilliseconds)} (shared call)";
    }

    static string? TimingSummary(ReviewResult r)
    {
        if (r.ReviewElapsedMilliseconds is not { } elapsed) return null;
        double provider = r.RequestTimings.Sum(t => t.ElapsedMilliseconds);
        string summary = $"Review phase: {Duration(elapsed)} | LLM calls: {Duration(provider)}";
        if (r.RequestTimings.Count > 0)
        {
            ReviewRequestTiming slowest = r.RequestTimings.MaxBy(t => t.ElapsedMilliseconds)!;
            summary += $" | Avg: {Duration(provider / r.RequestTimings.Count)} | Slowest: #{slowest.Request} ({Duration(slowest.ElapsedMilliseconds)})";
        }
        return summary;
    }

    public static void WriteRequestTimings(ReviewResult r, TextWriter w)
    {
        foreach (ReviewRequestTiming t in r.RequestTimings)
        {
            string tokens = $"tokens in/out {t.PromptTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}/{t.CompletionTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}";
            w.WriteLine(Safe($"Request #{t.Request}: {Duration(t.ElapsedMilliseconds)} | {t.Model} | {(t.StandardsOnly ? "standards" : "review")} | {t.Files} file(s) | {t.SystemCharacters + (long)t.UserCharacters} prompt chars | {tokens} | {t.AcceptedFindings} accepted before deduplication"));
        }
    }

    public static void WriteRichConsole(ReviewResult r, IAnsiConsole console)
    {
        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Findings[/]");
        table.AddColumn("[bold]Reviewed[/]");
        table.AddColumn("[bold]Skipped[/]");
        table.AddColumn("[bold]Rejected[/]");
        table.AddRow(
            new Markup($"[{(r.Findings.Count == 0 ? "green" : "yellow")}]{r.Findings.Count}[/]"),
            new Text(r.ReviewedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Text(r.SkippedFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Text(r.Rejected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        console.Write(new Markup("[bold cyan]SMARTWIZZ[/] [grey]| Code review[/]\n"));
        console.Write(table);
        if (r.ReviewedFiles == 0)
            console.Write(new Text("No reviewable files."));
        if (r.Models.Count > 0)
            console.Write(new Text(Safe("Model: " + string.Join(", ", r.Models) + "\n")));
        if (r.RulesetName is not null)
            console.Write(new Text(Safe($"Ruleset: {r.RulesetName} {r.RulesetVersion}\n")));
        console.Write(new Text($"{r.Requests} request(s)\n"));
        if (TimingSummary(r) is { } timingSummary)
            console.Write(new Text(timingSummary + "\n"));

        foreach (Finding f in r.Findings.OrderByDescending(f => f.Severity))
        {
            string color = f.Severity switch
            {
                Severity.Critical or Severity.High => "red",
                Severity.Medium => "yellow",
                _ => "cyan",
            };
            string lines = f.EndLine is { } e ? $"{f.StartLine}-{e}" : $"{f.StartLine}";
            var body = new Grid();
            body.AddColumn(new GridColumn().Width(Math.Max(1, Math.Min(110, console.Profile.Width) - 4)));
            body.AddRow(new Text(Safe(f.Title)));
            body.AddRow(new Text(Safe($"{f.FilePath}:{lines}")));
            body.AddRow(new Text(""));
            body.AddRow(new Text(Safe(f.Message)));
            if (f.SuggestedFix is { } fix)
            {
                body.AddRow(new Markup("[bold]Suggested fix (AI-generated, verify before applying)[/]"));
                body.AddRow(new Text(Safe(fix)));
            }
            body.AddRow(new Text(""));
            body.AddRow(new Text(Safe($"Rule: {f.RuleId} | Confidence: {f.Confidence:P0} | fp {f.Fingerprint}")));
            if (FindingTiming(r, f) is { } findingTiming)
                body.AddRow(new Text(findingTiming));
            var panel = new Panel(body)
            {
                Header = new PanelHeader($"[{color} bold]{f.Severity.ToString().ToUpperInvariant()}[/]"),
                Width = Math.Min(110, console.Profile.Width),
            };
            console.Write(panel);
        }
        if (r.SkippedFiles.Count > 0)
            console.Write(new Text(Safe("Skipped: " + string.Join(", ", r.SkippedFiles.Select(s => $"{s.Path} ({s.Reason})")) + "\n")));
        if (r.Rejected.Count > 0)
            console.Write(new Text(Safe("Rejected: " + string.Join("; ", r.Rejected) + "\n")));
    }

    public static void WriteConsole(ReviewResult r, TextWriter w)
    {
        w.WriteLine(Summary(r));
        if (TimingSummary(r) is { } timingSummary) w.WriteLine(timingSummary);
        if (r.Models.Count > 0)
            w.WriteLine($"Reviewed by: {string.Join(", ", r.Models)}");
        if (r.RulesetName is not null)
            w.WriteLine($"Ruleset: {r.RulesetName} {r.RulesetVersion}, {r.Requests} request(s)");
        foreach (Finding f in r.Findings)
        {
            string lines = f.EndLine is { } e ? $"{f.StartLine}-{e}" : $"{f.StartLine}";
            w.WriteLine();
            w.WriteLine($"[{f.Severity.ToString().ToUpperInvariant()}] {f.FilePath}:{lines}  {f.Title}");
            foreach (string line in f.Message.Split('\n'))
                w.WriteLine($"    {line.TrimEnd()}");
            if (f.SuggestedFix is { } fix)
            {
                w.WriteLine("    Suggested fix (AI-generated, verify before applying):");
                foreach (string line in fix.Split('\n'))
                    w.WriteLine($"        {line.TrimEnd()}");
            }
            w.WriteLine($"    ({f.RuleId}, confidence {f.Confidence:0.00}, fp {f.Fingerprint})");
            if (FindingTiming(r, f) is { } findingTiming) w.WriteLine("    " + findingTiming);
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
