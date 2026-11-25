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
