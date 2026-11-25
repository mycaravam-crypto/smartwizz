using System.Text.RegularExpressions;

namespace SmartWizz;

public static partial class RuleTaxonomy
{
    // Only explicitly equivalent categories. Broad configuration and off-by-one
    // rules are deliberately not equated with credentials or array bounds.
    public static string Canonical(string rule) => rule.ToLowerInvariant() switch
    {
        "hardcoded-password" or "hardcoded-secret" or "hardcoded-credential" => "hardcoded-credential",
        "sql-injection" or "unsafe-sql-query" => "sql-injection",
        "null-reference" or "null-dereference" => "null-dereference",
        "empty-catch" or "swallowed-exception" => "swallowed-exception",
        _ => rule.ToLowerInvariant(),
    };

    // Broad engineering checks are only reconciled when the validated source
    // anchor contains direct evidence of the narrower category.
    public static string Canonical(string rule, string source)
    {
        string category = Canonical(rule);
        if (category == "std-injection" &&
            (source.Contains("SELECT ", StringComparison.OrdinalIgnoreCase) ||
             source.Contains("INSERT INTO ", StringComparison.OrdinalIgnoreCase) ||
             source.Contains("DELETE FROM ", StringComparison.OrdinalIgnoreCase) ||
             source.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase)) &&
            (source.Contains("$\"", StringComparison.Ordinal) || source.Contains('+')))
            return "sql-injection";
        if (category is "std-hardcoded-config" or "hardcoded-config" && CredentialAssignment().IsMatch(source))
            return "hardcoded-credential";
        if (category == "index-out-of-range" && InclusiveLengthLoop().IsMatch(source))
            return "off-by-one";
        return category;
    }

    // Historical checks corroborate a narrow lexical pattern, not program semantics.
    // Never interpret a negative result as proof of resolution.
    internal static string HistoricalPattern(string category, string source)
    {
        if (source.Length > 4096 || source.TrimStart().StartsWith("//", StringComparison.Ordinal) ||
            source.TrimStart().StartsWith("/*", StringComparison.Ordinal) || source.TrimStart().StartsWith('*'))
            return "not-checked";
        bool? matched = category switch
        {
            "hardcoded-credential" => CredentialAssignment().IsMatch(source),
            "off-by-one" => InclusiveLengthLoop().IsMatch(source),
            "sql-injection" => Canonical("std-injection", source) == "sql-injection",
            _ => null,
        };
        return matched switch
        {
            true => "pattern-present",
            false => "pattern-not-confirmed",
            null => "not-checked",
        };
    }

    // Restrict the alias to a zero-based increasing loop with the same index in its condition.
    [GeneratedRegex(@"^\s*for\s*\(\s*(?:int|var)\s+(?<index>\w+)\s*=\s*0\s*;\s*\k<index>\s*<=\s*\w+\.Length\s*;\s*\k<index>\s*\+\+\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex InclusiveLengthLoop();

    // Narrow, best-effort protection for direct quoted credential assignments.
    // Do not persist the captured literals or treat this as a general secret scanner.
    [GeneratedRegex("""\b\w*(?:password|secret|api_?key|access_?token)\w*\s*(?:\{\s*get;\s*(?:(?:private\s+)?set;|init;)?\s*\}\s*)?=\s*"(?<value>(?:\\.|[^"\\\r\n]){4,2048})" """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex CredentialAssignment();

    internal static string[] CredentialLiterals(IEnumerable<string> lines) =>
        [.. lines.SelectMany(line => CredentialAssignment().Matches(line).Cast<Match>())
            .Select(match => match.Groups["value"].Value).Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)];

    internal static string Redact(string text, IEnumerable<string> literals)
    {
        foreach (string literal in literals)
            text = text.Replace(literal, "[REDACTED]", StringComparison.Ordinal);
        return text;
    }

    // Exact aliases supported by the bundled taxonomy. Unknown model text must
    // never be stored, even when it is presented as a rule identifier.
    public static bool IsCacheable(string rule) => rule is
        "hardcoded-password" or "hardcoded-secret" or "hardcoded-credential" or
        "hardcoded-config" or "std-hardcoded-config" or "std-injection" or
        "sql-injection" or "unsafe-sql-query" or "null-reference" or "null-dereference" or
        "empty-catch" or "swallowed-exception" or "missing-test" or "off-by-one" or
        "index-out-of-range";

    // Unknown model-generated identifiers are not trusted metadata.
    public static string StorageCategory(string rule) => Canonical(rule) switch
    {
        "hardcoded-credential" => "hardcoded-credential",
        "sql-injection" => "sql-injection",
        "null-dereference" => "null-dereference",
        "swallowed-exception" => "swallowed-exception",
        "off-by-one" => "off-by-one",
        _ => "other",
    };
}
