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
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Pooling = false,
            }.ToString()))
            {
                db.Open();
                using SqliteCommand query = db.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM findings";
                Assert.Equal(2L, (long)query.ExecuteScalar()!);
            }
            // Close native SQLite handles before inspecting the file on Windows.
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

    [Fact]
    public void Legacy_broad_rule_is_reconciled_at_verified_credential_anchor()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string database = Path.Combine(directory, "history.db");
        byte[] key = new byte[32];
        try
        {
            const string source = "before\nstring Password = \"FAKE_TEST_SECRET\";\nafter";
            ReviewResult old = Make("std-hardcoded-config", 59);
            old.FindingAnchors["fp"] = source;
            FindingStore.Record(database, key, "repo", "scope", "revision", old);
            ReviewResult current = Make("hardcoded-config", 59);
            current.FindingAnchors["fp"] = source;
            current.FindingCategories["fp"] = "hardcoded-credential";
            FindingStore.Record(database, key, "repo", "scope", "revision", current);
            Assert.Equal(1, current.KnownFindings);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Pooling = false,
            }.ToString());
            db.Open();
            using SqliteCommand query = db.CreateCommand();
            query.CommandText = "SELECT observations FROM findings";
            Assert.Equal(2L, (long)query.ExecuteScalar()!);
            query.CommandText = "SELECT COUNT(*) FROM findings";
            Assert.Equal(1L, (long)query.ExecuteScalar()!);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("std-injection", """return $"SELECT * FROM T WHERE Id = {id}";""", "sql-injection")]
    [InlineData("std-injection", """Process.Start("cmd", input);""", "std-injection")]
    [InlineData("std-hardcoded-config", """string Password = "FAKE_TEST_SECRET";""", "hardcoded-credential")]
    [InlineData("hardcoded-config", """string Password = config["password"];""", "hardcoded-config")]
    [InlineData("std-hardcoded-config", """string Endpoint = "http://example.test";""", "std-hardcoded-config")]
    public void Broad_rules_require_source_evidence(string rule, string source, string expected) =>
        Assert.Equal(expected, RuleTaxonomy.Canonical(rule, source));

    [Fact]
    public void Credential_redaction_removes_direct_literal_from_model_text()
    {
        var file = new FileDiff { Path = "test.cs" };
        file.AddedLines.Add(1);
        file.NewLines.Add(1, """string Password = "FAKE_TEST_SECRET";""");
        var files = new Dictionary<string, FileDiff>
        {
            [file.Path] = file,
        };
        Finding finding = Assert.Single(Review.Parse("""
            {"findings":[{"ruleId":"hardcoded-secret","severity":"high","title":"FAKE_TEST_SECRET","message":"Password FAKE_TEST_SECRET exposed","filePath":"test.cs","startLine":1,"confidence":0.95,"suggestedFix":"remove FAKE_TEST_SECRET"}]}
            """, files, 0.5, []));
        Assert.DoesNotContain("FAKE_TEST_SECRET", finding.Title + finding.Message + finding.SuggestedFix, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", finding.Message);
    }

    [Theory]
    [InlineData("hardcoded-password", "hardcoded-credential")]
    [InlineData("hardcoded-config", "hardcoded-config")]
    [InlineData("off-by-one", "off-by-one")]
    public void Taxonomy_does_not_merge_broad_categories(string input, string expected) =>
        Assert.Equal(expected, RuleTaxonomy.Canonical(input));
}
