using System.IO.Enumeration;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartWizz;

// One model-facing check: a short instruction that covers one or more catalog rules.
// The first rule is the one findings are reported under.
public sealed class LlmCheck
{
    public string Id { get; set; } = "";
    public string[] Rules { get; set; } = [];
    public string[] Files { get; set; } = [];
    // Regexes matched against added lines; the check is sent only if one matches. Empty = always sent.
    public string[] Triggers { get; set; } = [];
    public string Severity { get; set; } = "";
    public string Check { get; set; } = "";
    public string? Bad { get; set; }
    public string? Good { get; set; }
    public string[] Allowed { get; set; } = [];
}

public sealed class UncheckedRule
{
    public string Rule { get; set; } = "";
    public string Reason { get; set; } = "";
}

// The catalog's llm rules, aggregated into fewer, shorter, trigger-gated checks for small models.
public sealed class LlmChecks
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly TimeSpan TriggerTimeout = TimeSpan.FromMilliseconds(100);

    sealed class Document
    {
        public int SchemaVersion { get; set; }
        public Dictionary<string, string[]> FileGroups { get; set; } = [];
        public List<LlmCheck> Checks { get; set; } = [];
        public List<UncheckedRule> NotChecked { get; set; } = [];
    }

    public sealed record Compiled(LlmCheck Source, Severity Severity, string[] Files, Regex[] Triggers)
    {
        public bool AppliesTo(FileDiff file)
        {
            string name = Path.GetFileName(file.Path);
            if (!Files.Any(p => FileSystemName.MatchesSimpleExpression(p, file.Path, ignoreCase: true)
                    || FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)))
                return false;
            if (Triggers.Length == 0) return true;
            foreach (int line in file.AddedLines)
            {
                if (!file.NewLines.TryGetValue(line, out string? text)) continue;
                foreach (Regex trigger in Triggers)
                {
                    try
                    {
                        if (trigger.IsMatch(text)) return true;
                    }
                    // Diff text is untrusted; a pathological line must not hang the review. Prefer sending the check.
                    catch (RegexMatchTimeoutException) { return true; }
                }
            }
            return false;
        }
    }

    public IReadOnlyList<Compiled> Checks { get; }

    LlmChecks(IReadOnlyList<Compiled> checks) => Checks = checks;

    public static LlmChecks Parse(string json, IReadOnlyList<EngineeringRule> catalogRules)
    {
        Document? doc;
        try
        {
            doc = JsonSerializer.Deserialize<Document>(json, Json);
        }
        catch (JsonException e)
        {
            throw Error("invalid JSON", e);
        }
        if (doc is null || doc.SchemaVersion != 1)
            throw Error("unsupported schemaVersion");

        foreach ((string name, string[] globs) in doc.FileGroups)
            if (!Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)
                || globs is null || globs.Length == 0 || globs.Any(string.IsNullOrWhiteSpace))
                throw Error($"invalid file group: {name}");

        var llmRules = catalogRules.Where(r => r.Detection == "llm").ToDictionary(r => r.Id, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        void Cover(string ruleId, string owner)
        {
            if (!llmRules.ContainsKey(ruleId))
                throw Error($"{owner}: {ruleId} is not an llm rule in the catalog");
            if (!covered.Add(ruleId))
                throw Error($"{owner}: {ruleId} is already covered by another check");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var compiled = new List<Compiled>();
        foreach (LlmCheck check in doc.Checks)
        {
            if (!Regex.IsMatch(check.Id, "^std-[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant) || !ids.Add(check.Id))
                throw Error($"invalid or duplicate check id: {check.Id}");
            if (string.IsNullOrWhiteSpace(check.Check) || check.Rules is null || check.Rules.Length == 0
                || check.Files is null || check.Files.Length == 0 || check.Triggers is null || check.Allowed is null)
                throw Error($"{check.Id}: check, rules and files are required");
            if (Review.ParseSeverity(check.Severity) is not { } severity)
                throw Error($"{check.Id}: invalid severity {check.Severity}");
            foreach (string rule in check.Rules) Cover(rule, check.Id);

            var files = new List<string>();
            foreach (string entry in check.Files)
            {
                if (string.IsNullOrWhiteSpace(entry)) throw Error($"{check.Id}: empty file pattern");
                if (!entry.StartsWith('@')) files.Add(entry);
                else if (doc.FileGroups.TryGetValue(entry[1..], out string[]? group)) files.AddRange(group);
                else throw Error($"{check.Id}: unknown file group {entry}");
            }

            var triggers = new Regex[check.Triggers.Length];
            for (int i = 0; i < triggers.Length; i++)
            {
                try
                {
                    triggers[i] = new Regex(check.Triggers[i], RegexOptions.CultureInvariant, TriggerTimeout);
                }
                catch (ArgumentException e)
                {
                    throw Error($"{check.Id}: invalid trigger {check.Triggers[i]}", e);
                }
            }
            compiled.Add(new Compiled(check, severity, [.. files.Distinct(StringComparer.Ordinal)], triggers));
        }

        foreach (UncheckedRule skipped in doc.NotChecked)
        {
            if (string.IsNullOrWhiteSpace(skipped.Reason))
                throw Error($"notChecked {skipped.Rule}: reason is required");
            Cover(skipped.Rule, "notChecked");
        }
        if (llmRules.Keys.Where(id => !covered.Contains(id)).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } missing)
            throw Error($"llm rules without a check or notChecked entry: {string.Join(", ", missing)}");
        if (compiled.Count == 0)
            throw Error("no checks defined");
        return new LlmChecks(compiled);
    }

    static SmartWizzException Error(string message, Exception? inner = null) =>
        new(FailureKind.Configuration, $"invalid LLM checks: {message}", inner);
}
