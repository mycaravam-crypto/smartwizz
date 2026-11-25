using Microsoft.Data.Sqlite;

namespace SmartWizz.Tests;

public class FindingStoreTests
{
    [Fact]
    public void History_recognizes_moved_alias_and_does_not_store_source_or_model_text()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string database = Path.Combine(directory, "history.db");
        byte[] key = new byte[32];
        try
        {
            ReviewResult first = Make("hardcoded-password", 59);
            FindingStore.Record(database, key, "repo-a", "scope", "revision", first);
            ReviewResult second = Make("hardcoded-secret", 79);
            FindingStore.Record(database, key, "repo-a", "scope", "revision", second);
            Assert.Equal(1, second.KnownFindings);
            Assert.Equal(first.PersistentFindingIds["fp"], second.PersistentFindingIds["fp"]);
            ReviewResult third = Make("hardcoded-secret", 79);
            FindingStore.Record(database, key, "repo-b", "scope", "revision", third);
            Assert.Equal(0, third.KnownFindings);
            Assert.NotEqual(second.PersistentFindingIds["fp"], third.PersistentFindingIds["fp"]);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            db.Open();
            using SqliteCommand query = db.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM findings";
            Assert.Equal(2L, (long)query.ExecuteScalar()!);
            string bytes = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(database));
            Assert.DoesNotContain("PRIVATE_SOURCE_SENTINEL", bytes, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE_MODEL_SENTINEL", bytes, StringComparison.Ordinal);
            Assert.DoesNotContain("PrivateFile.cs", bytes, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    static ReviewResult Make(string rule, int line)
    {
        var result = new ReviewResult();
        result.Findings.Add(new Finding(rule, Severity.High, "PRIVATE_MODEL_SENTINEL", "PRIVATE_MODEL_SENTINEL",
            "PrivateFile.cs", line, null, 0.95, "fp", "PRIVATE_SOURCE_SENTINEL"));
        result.FindingAnchors.Add("fp", "PRIVATE_SOURCE_SENTINEL");
        return result;
    }

    [Theory]
    [InlineData("hardcoded-password", "hardcoded-credential")]
    [InlineData("hardcoded-config", "hardcoded-config")]
    [InlineData("off-by-one", "off-by-one")]
    public void Taxonomy_does_not_merge_broad_categories(string input, string expected) =>
        Assert.Equal(expected, RuleTaxonomy.Canonical(input));
}
