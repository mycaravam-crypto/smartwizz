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
            ReviewResult cached = Make("hardcoded-secret", 79);
            cached.CacheHit = true;
            FindingStore.ReadIdentities(database, key, "repo-a", "scope", cached, default);
            Assert.Equal(second.PersistentFindingIds["fp"], cached.PersistentFindingIds["fp"]);
            Assert.Equal(1, cached.KnownFindings);
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
                query.CommandText = "SELECT SUM(observations) FROM findings";
                Assert.Equal(3L, (long)query.ExecuteScalar()!);
            }
            // Close native SQLite handles before inspecting the file on Windows.
            string bytes = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(database));
            Assert.DoesNotContain("PRIVATE_SOURCE_SENTINEL", bytes, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE_MODEL_SENTINEL", bytes, StringComparison.Ordinal);
            Assert.DoesNotContain("PrivateFile.cs", bytes, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Missing_observation_is_unverified_not_resolved_and_scope_isolated()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string database = Path.Combine(directory, "history.db");
        byte[] key = new byte[32];
        try
        {
            ReviewResult initial = Make("hardcoded-secret", 59);
            FindingStore.Record(database, key, "repo", "scope", "revision", initial);
            var absent = new ReviewResult();
            FindingStore.Record(database, key, "repo", "scope", "revision", absent);
            HistoricalFinding historical = Assert.Single(Assert.IsType<List<HistoricalFinding>>(absent.UnverifiedHistory));
            Assert.Equal(initial.PersistentFindingIds["fp"], historical.Id);
            Assert.Equal(1L, historical.Observations);
            Assert.Equal(Severity.High, historical.LastObservedSeverity);
            Assert.Empty(absent.Findings);
            var unrelated = new ReviewResult();
            FindingStore.Record(database, key, "repo", "other-scope", "revision", unrelated);
            Assert.Empty(Assert.IsType<List<HistoricalFinding>>(unrelated.UnverifiedHistory));
            var cached = new ReviewResult { CacheHit = true };
            FindingStore.ReadIdentities(database, key, "repo", "scope", cached, default);
            Assert.Null(cached.UnverifiedHistory);
            ReviewResult rediscovered = Make("hardcoded-secret", 59);
            FindingStore.Record(database, key, "repo", "scope", "revision", rediscovered);
            Assert.Equal(1, rediscovered.KnownFindings);
            Assert.Empty(Assert.IsType<List<HistoricalFinding>>(rediscovered.UnverifiedHistory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Stable_policy_scope_adopts_legacy_identity_and_survives_review_configuration_changes()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string database = Path.Combine(directory, "history.db");
        byte[] key = new byte[32];
        try
        {
            ReviewResult initial = Make("hardcoded-secret", 59);
            FindingStore.Record(database, key, "repo", "old-exact", "revision", initial);
            string policy = FindingStore.PolicyScope("ruleset", null);
            ReviewResult cached = Make("hardcoded-secret", 59);
            cached.CacheHit = true;
            FindingStore.ReadIdentities(database, key, "repo", policy, cached, "old-exact", default);
            Assert.Equal(initial.PersistentFindingIds["fp"], cached.PersistentFindingIds["fp"]);
            ReviewResult adopted = Make("hardcoded-secret", 59);
            FindingStore.Record(database, key, "repo", policy, "revision", adopted, legacyScope: "old-exact");
            Assert.Equal(initial.PersistentFindingIds["fp"], adopted.PersistentFindingIds["fp"]);
            ReviewResult later = Make("hardcoded-secret", 59);
            FindingStore.Record(database, key, "repo", policy, "revision", later, legacyScope: "different-exact");
            Assert.Equal(initial.PersistentFindingIds["fp"], later.PersistentFindingIds["fp"]);
            Assert.Equal(1, later.KnownFindings);
            ReviewResult changedPolicy = Make("hardcoded-secret", 59);
            FindingStore.Record(database, key, "repo", FindingStore.PolicyScope("other-ruleset", null), "revision",
                changedPolicy, legacyScope: "old-exact");
            Assert.NotEqual(initial.PersistentFindingIds["fp"], changedPolicy.PersistentFindingIds["fp"]);
            Assert.Equal(0, changedPolicy.KnownFindings);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Pooling = false,
            }.ToString());
            db.Open();
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = "SELECT SUM(observations) FROM findings";
            Assert.Equal(4L, (long)command.ExecuteScalar()!);
            command.CommandText = "SELECT COUNT(*) FROM findings";
            Assert.Equal(2L, (long)command.ExecuteScalar()!);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Historical_anchor_evidence_is_bounded_and_never_resolves_a_finding()
    {
        byte[] key = new byte[32];
        var file = new FileDiff { Path = "PrivateFile.cs" };
        file.NewLines.Add(58, "before");
        file.NewLines.Add(59, "string Password = \"FAKE_TEST_SECRET\";");
        file.NewLines.Add(60, "after");
        file.AddedLines.Add(59);
        string anchor = FindingStore.Hash(key, "anchor-v1", file.Path, "hardcoded-credential", Review.SourceAnchor(file, 59));
        int budget = 10;
        Assert.Equal(("exact-match", (int?)59), FindingStore.MatchHistoricalAnchor(key, file, "hardcoded-credential", anchor, ref budget, default));
        Assert.Equal(9, budget);
        Assert.Equal(("not-in-reviewed-diff", (int?)null), FindingStore.MatchHistoricalAnchor(key, null, "hardcoded-credential", anchor, ref budget, default));
        file.NewLines[59] = "string Password = configurationValue;";
        Assert.Equal(("not-matched", (int?)null), FindingStore.MatchHistoricalAnchor(key, file, "hardcoded-credential", anchor, ref budget, default));
        budget = 0;
        Assert.Equal(("not-checked", (int?)null), FindingStore.MatchHistoricalAnchor(key, file, "hardcoded-credential", anchor, ref budget, default));
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

    [Fact]
    public void Inclusive_loop_alias_migration_preserves_canonical_identity_and_observations()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string database = Path.Combine(directory, "history.db");
        byte[] key = new byte[32];
        try
        {
            const string line = "for (int i = 0; i <= values.Length; i++)";
            const string source = "before\n" + line + "\nresult += values[i];";
            ReviewResult canonical = Make("off-by-one", 71);
            canonical.FindingAnchors["fp"] = source;
            FindingStore.Record(database, key, "repo", "scope", "revision", canonical);
            ReviewResult legacy = Make("index-out-of-range", 71);
            legacy.FindingAnchors["fp"] = source;
            FindingStore.Record(database, key, "repo", "scope", "revision", legacy);
            ReviewResult current = Make("index-out-of-range", 71);
            current.FindingAnchors["fp"] = source;
            current.FindingCategories["fp"] = RuleTaxonomy.Canonical("index-out-of-range", line);
            FindingStore.Record(database, key, "repo", "scope", "revision", current);
            Assert.Equal(1, current.KnownFindings);
            Assert.Equal(canonical.PersistentFindingIds["fp"], current.PersistentFindingIds["fp"]);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Pooling = false,
            }.ToString());
            db.Open();
            using SqliteCommand query = db.CreateCommand();
            query.CommandText = "SELECT observations FROM findings";
            Assert.Equal(3L, (long)query.ExecuteScalar()!);
            query.CommandText = "SELECT COUNT(*) FROM findings";
            Assert.Equal(1L, (long)query.ExecuteScalar()!);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("index-out-of-range", "for (int i = 0; i <= values.Length; i++)", "off-by-one")]
    [InlineData("index-out-of-range", "for (int i = 0; i < values.Length; i++)", "index-out-of-range")]
    [InlineData("index-out-of-range", "for (int i = 0; j <= values.Length; i++)", "index-out-of-range")]
    [InlineData("index-out-of-range", "return values[index];", "index-out-of-range")]
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
