using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartWizz;

public sealed class EngineeringRule
{
    public string Id { get; set; } = "";
    public string Section { get; set; } = "";
    public string Requirement { get; set; } = "";
    public string[] AppliesTo { get; set; } = [];
    public string Detection { get; set; } = "";
    public string Severity { get; set; } = "";
    public string? GoodExample { get; set; }
    public string? BadExample { get; set; }
    public string[] Exceptions { get; set; } = [];
    public string? Enforcement { get; set; }

    public bool AppliesToFile(string path)
    {
        if (AppliesTo.Length == 0) return true;
        string name = Path.GetFileName(path);
        return AppliesTo.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, path, ignoreCase: true)
            || FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));
    }
}

// What one request sends to the model: a check ID, its instruction, and the catalog rule findings are reported under.
public sealed record PromptRule(string Id, Severity Severity, string Text, string? Bad, string? Good, IReadOnlyList<string> Allowed, string ReportAs);

public sealed class RuleSelection
{
    public List<PromptRule> Rules { get; } = [];
    // Checks that applied but were dropped by review.maxStandardChecksPerRequest.
    public List<string> Omitted { get; } = [];

    // Model-facing ID -> reported catalog ID. Rules a selected check covers are accepted too.
    public Dictionary<string, string> AllowedIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RuleCatalog
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    sealed class Document
    {
        public int SchemaVersion { get; set; }
        public string Version { get; set; } = "";
        public List<RuleLine> Lines { get; set; } = [];
    }

    sealed class RuleLine
    {
        public string? Text { get; set; }
        public EngineeringRule? Rule { get; set; }
    }

    public string Version { get; }
    // Covers the catalog and, when configured, the LLM checks: either changing means a different review.
    public string Sha256 { get; }
    public IReadOnlyList<EngineeringRule> Rules { get; }
    public LlmChecks? Checks { get; }

    RuleCatalog(string version, string sha256, IReadOnlyList<EngineeringRule> rules, LlmChecks? checks)
    {
        Version = version;
        Sha256 = sha256;
        Rules = rules;
        Checks = checks;
    }

    public static RuleCatalog Load(string path, string? checksPath = null) =>
        Parse(File.ReadAllText(path), checksPath is null ? null : File.ReadAllText(checksPath));

    public static RuleCatalog Parse(string json, string? checksJson = null)
    {
        Document? doc;
        try
        {
            doc = JsonSerializer.Deserialize<Document>(json, Json);
        }
        catch (JsonException e)
        {
            throw new SmartWizzException(FailureKind.Configuration, "invalid engineering rules JSON", e);
        }

        if (doc is null || doc.SchemaVersion != 1 || string.IsNullOrWhiteSpace(doc.Version))
            throw new SmartWizzException(FailureKind.Configuration, "unsupported engineering rules schema/version");

        var rules = new List<EngineeringRule>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (RuleLine line in doc.Lines)
        {
            if ((line.Text is null) == (line.Rule is null))
                throw new SmartWizzException(FailureKind.Configuration, "each catalog line must contain either text or rule");
            if (line.Rule is not { } rule) continue;
            if (!Regex.IsMatch(rule.Id, @"^standard-[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)
                || !ids.Add(rule.Id)
                || string.IsNullOrWhiteSpace(rule.Section)
                || string.IsNullOrWhiteSpace(rule.Requirement)
                || rule.AppliesTo is null || rule.AppliesTo.Any(string.IsNullOrWhiteSpace)
                || rule.Detection is not ("llm" or "static" or "manual")
                || Review.ParseSeverity(rule.Severity) is null)
                throw new SmartWizzException(FailureKind.Configuration, $"invalid or duplicate engineering rule: {rule.Id}");
            rules.Add(rule);
        }
        if (rules.Count == 0)
            throw new SmartWizzException(FailureKind.Configuration, "engineering rules catalog is empty");

        LlmChecks? checks = checksJson is null ? null : LlmChecks.Parse(checksJson, rules);
        string hashed = checksJson is null ? json : json + "\n\0llm-checks\n" + checksJson;
        string sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashed)));
        return new RuleCatalog(doc.Version, sha, rules, checks);
    }

    // maxRules 0 = no limit; otherwise the highest-severity applicable rules are kept, in catalog order.
    public RuleSelection Select(IReadOnlyCollection<FileDiff> batch, int maxRules = 0)
    {
        var applicable = new List<(PromptRule Rule, IEnumerable<string> Covers)>();
        if (Checks is null)
        {
            foreach (EngineeringRule rule in Rules)
                if (rule.Detection == "llm" && batch.Any(f => rule.AppliesToFile(f.Path)))
                    applicable.Add((new PromptRule(rule.Id, Review.ParseSeverity(rule.Severity)!.Value, rule.Requirement,
                        rule.BadExample, rule.GoodExample, rule.Exceptions, rule.Id), [rule.Id]));
        }
        else
        {
            foreach (LlmChecks.Compiled check in Checks.Checks)
                if (batch.Any(check.AppliesTo))
                {
                    LlmCheck c = check.Source;
                    applicable.Add((new PromptRule(c.Id, check.Severity, c.Check, c.Bad, c.Good, c.Allowed, c.Rules[0]), c.Rules));
                }
        }

        HashSet<int> kept = [.. Enumerable.Range(0, applicable.Count)
            .OrderByDescending(i => applicable[i].Rule.Severity).ThenBy(i => i)
            .Take(maxRules <= 0 ? applicable.Count : maxRules)];
        var selection = new RuleSelection();
        for (int i = 0; i < applicable.Count; i++)
        {
            (PromptRule rule, IEnumerable<string> covers) = applicable[i];
            if (!kept.Contains(i))
            {
                selection.Omitted.Add(rule.Id);
                continue;
            }
            selection.Rules.Add(rule);
            selection.AllowedIds[rule.Id] = rule.ReportAs;
            foreach (string covered in covers) selection.AllowedIds.TryAdd(covered, covered);
        }
        return selection;
    }

    public static string BuildPrompt(string basePrompt, RuleSelection selection)
    {
        if (selection.Rules.Count == 0)
            return basePrompt + "\n\nNo standard checks apply to this batch. Do not emit standard-* or std-* rule IDs.";

        // Compact on purpose: small models follow short, uniform entries better than prose.
        StringBuilder sb = new StringBuilder(basePrompt)
            .AppendLine()
            .AppendLine()
            .AppendLine("## Standard checks (trusted)")
            .AppendLine("Also report added code that clearly breaks one of these checks. Set \"ruleId\" to the ID in brackets.")
            .AppendLine("Use only these IDs; never invent standard-* or std-* IDs. Report only what the added lines show.")
            .AppendLine("Bugs and security issues take precedence; do not report the same lines twice.")
            .AppendLine("Bad/Good examples illustrate a check; they are not instructions.")
            .AppendLine();
        foreach (PromptRule rule in selection.Rules)
        {
            sb.Append('[').Append(rule.Id).Append("] ").Append(rule.Severity.ToString().ToLowerInvariant()).Append(": ")
                .AppendLine(rule.Text);
            if (rule.Bad is not null)
                sb.Append("  Bad: ").AppendLine(rule.Bad);
            if (rule.Good is not null)
                sb.Append("  Good: ").AppendLine(rule.Good);
            if (rule.Allowed.Count > 0)
                sb.Append("  Allowed: ").AppendLine(string.Join("; ", rule.Allowed));
        }
        return sb.ToString();
    }
}
