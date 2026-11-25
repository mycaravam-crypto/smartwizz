namespace SmartWizz;

public static class RuleTaxonomy
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
            (source.Contains("$\"", StringComparison.Ordinal) || source.Contains("+", StringComparison.Ordinal)))
            return "sql-injection";
        if (category is "std-hardcoded-config" or "hardcoded-config" &&
            (source.Contains("password", StringComparison.OrdinalIgnoreCase) ||
             source.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
             source.Contains("apiKey", StringComparison.OrdinalIgnoreCase)) &&
            source.Contains("=", StringComparison.Ordinal) && source.Contains('"'))
            return "hardcoded-credential";
        return category;
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
        _ => "other",
    };
}
