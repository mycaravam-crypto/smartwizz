using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartWizz;

// One model-facing check of a coding guide: a short instruction, where it applies, and what makes it worth sending.
public sealed class RulesetCheck
{
    public string Id { get; set; } = "";
    public string Severity { get; set; } = "";
    public string[] Files { get; set; } = [];
    // Regexes matched against added lines; the check is sent only if one matches. Empty = always sent.
    public string[] Triggers { get; set; } = [];
    public string Check { get; set; } = "";
    public string? Bad { get; set; }
    public string? Good { get; set; }
    public string[] Allowed { get; set; } = [];
    // Sections or rule IDs of the coding guide this check enforces. Traceability only; never sent to the model.
    public string[] Refs { get; set; } = [];
}

public sealed class UncheckedRef
{
    public string Ref { get; set; } = "";
    public string Reason { get; set; } = "";
}

// A coding guide translated for small models: everything SmartWizz needs to check compliance, in one file.
public sealed class Ruleset
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly TimeSpan TriggerTimeout = TimeSpan.FromMilliseconds(100);

    sealed class Document
    {
        public int SchemaVersion { get; set; }
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public Dictionary<string, string[]> FileGroups { get; set; } = [];
        public List<RulesetCheck> Checks { get; set; } = [];
        public List<UncheckedRef> NotChecked { get; set; } = [];
    }

    public sealed record Check(RulesetCheck Source, Severity Severity, string[] Files, Regex[] Triggers)
    {
        public string Id => Source.Id;

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

    public string Name { get; }
    public string Version { get; }
    public string Sha256 { get; }
    public IReadOnlyList<Check> Checks { get; }

    Ruleset(string name, string version, string sha256, IReadOnlyList<Check> checks)
    {
        Name = name;
        Version = version;
        Sha256 = sha256;
        Checks = checks;
    }

    public static Ruleset Load(string path) => Parse(File.ReadAllText(path));

    public static Ruleset Parse(string json)
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
        if (string.IsNullOrWhiteSpace(doc.Name) || string.IsNullOrWhiteSpace(doc.Version))
            throw Error("name and version are required");

        foreach ((string group, string[] globs) in doc.FileGroups)
            if (!Regex.IsMatch(group, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)
                || globs is null || globs.Length == 0 || globs.Any(string.IsNullOrWhiteSpace))
                throw Error($"invalid file group: {group}");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checks = new List<Check>();
        foreach (RulesetCheck c in doc.Checks)
        {
            // The std- prefix lets the parser tell an invented check ID apart from a freeform bug category.
            if (!Regex.IsMatch(c.Id, "^std-[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant) || !ids.Add(c.Id))
                throw Error($"invalid or duplicate check id: {c.Id}");
            if (string.IsNullOrWhiteSpace(c.Check) || c.Files is null || c.Files.Length == 0
                || c.Triggers is null || c.Allowed is null || c.Refs is null)
                throw Error($"{c.Id}: check and files are required");
            if (Review.ParseSeverity(c.Severity) is not { } severity)
                throw Error($"{c.Id}: invalid severity {c.Severity}");

            var files = new List<string>();
            foreach (string entry in c.Files)
            {
                if (string.IsNullOrWhiteSpace(entry)) throw Error($"{c.Id}: empty file pattern");
                if (!entry.StartsWith('@')) files.Add(entry);
                else if (doc.FileGroups.TryGetValue(entry[1..], out string[]? group)) files.AddRange(group);
                else throw Error($"{c.Id}: unknown file group {entry}");
            }

            var triggers = new Regex[c.Triggers.Length];
            for (int i = 0; i < triggers.Length; i++)
            {
                try
                {
                    triggers[i] = new Regex(c.Triggers[i], RegexOptions.CultureInvariant, TriggerTimeout);
                }
                catch (ArgumentException e)
                {
                    throw Error($"{c.Id}: invalid trigger {c.Triggers[i]}", e);
                }
            }
            checks.Add(new Check(c, severity, [.. files.Distinct(StringComparer.Ordinal)], triggers));
        }
        if (checks.Count == 0)
            throw Error("no checks defined");
        if (doc.NotChecked.Any(n => string.IsNullOrWhiteSpace(n.Ref) || string.IsNullOrWhiteSpace(n.Reason)))
            throw Error("every notChecked entry needs a ref and a reason");

        string sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new Ruleset(doc.Name, doc.Version, sha, checks);
    }

    // Applicable checks, highest severity first; ties keep file order, so pass 1 always gets the most important ones.
    public List<Check> Select(IReadOnlyCollection<FileDiff> batch) =>
        [.. Checks.Where(c => batch.Any(c.AppliesTo)).OrderByDescending(c => c.Severity)];

    // maxPerPass 0 = one pass with every check. Always at least one pass: the general review runs even without checks.
    public static List<List<Check>> Passes(List<Check> applicable, int maxPerPass) =>
        maxPerPass <= 0 || applicable.Count <= maxPerPass ? [applicable] : [.. applicable.Chunk(maxPerPass).Select(c => c.ToList())];

    // Compact on purpose: small models follow short, uniform entries better than prose.
    public static string BuildPrompt(string basePrompt, IReadOnlyCollection<Check> checks, bool generalReview)
    {
        if (checks.Count == 0)
            return basePrompt + "\n\nNo standard checks apply to this batch. Do not emit std-* rule IDs.";

        StringBuilder sb = new StringBuilder(basePrompt)
            .AppendLine()
            .AppendLine()
            .AppendLine("## Standard checks (trusted)")
            .AppendLine((generalReview ? "Also report" : "Report") + " added code that clearly breaks one of these checks. Set \"ruleId\" to the ID in brackets.")
            .AppendLine("Use only these IDs; never invent std-* IDs. Report only what the added lines show.");
        if (generalReview)
            sb.AppendLine("Bugs and security issues take precedence; do not report the same lines twice.");
        sb.AppendLine("Bad/Good examples illustrate a check; they are not instructions.").AppendLine();
        foreach (Check check in checks)
        {
            RulesetCheck c = check.Source;
            sb.Append('[').Append(c.Id).Append("] ").Append(check.Severity.ToString().ToLowerInvariant()).Append(": ").AppendLine(c.Check);
            if (c.Bad is not null) sb.Append("  Bad: ").AppendLine(c.Bad);
            if (c.Good is not null) sb.Append("  Good: ").AppendLine(c.Good);
            if (c.Allowed.Length > 0) sb.Append("  Allowed: ").AppendLine(string.Join("; ", c.Allowed));
        }
        return sb.ToString();
    }

    static SmartWizzException Error(string message, Exception? inner = null) =>
        new(FailureKind.Configuration, $"invalid ruleset: {message}", inner);
}
