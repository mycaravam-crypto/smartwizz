namespace SmartWizz.Tests;

public sealed class LlmChecksTests
{
    const string Catalog = """
        {
          "schemaVersion": 1,
          "version": "2.0.0",
          "lines": [
            { "rule": { "id": "standard-catch", "section": "4.1", "requirement": "Never swallow exceptions",
              "appliesTo": [], "detection": "llm", "severity": "medium" } },
            { "rule": { "id": "standard-cause", "section": "4.1", "requirement": "Keep the inner exception",
              "appliesTo": [], "detection": "llm", "severity": "medium" } },
            { "rule": { "id": "standard-secret", "section": "7", "requirement": "No secrets in code",
              "appliesTo": [], "detection": "llm", "severity": "high" } },
            { "rule": { "id": "standard-tests", "section": "8", "requirement": "Deterministic tests",
              "appliesTo": [], "detection": "llm", "severity": "medium" } },
            { "rule": { "id": "standard-graph", "section": "5", "requirement": "No cycles",
              "appliesTo": [], "detection": "llm", "severity": "medium" } },
            { "rule": { "id": "standard-static", "section": "9", "requirement": "Compile with nullability",
              "appliesTo": ["*.cs"], "detection": "static", "severity": "medium" } }
          ]
        }
        """;

    const string Checks = """
        {
          "schemaVersion": 1,
          "fileGroups": { "code": ["*.cs", "*.ts"], "tests": ["*Tests.cs"] },
          "checks": [
            { "id": "std-swallowed", "rules": ["standard-catch", "standard-cause"], "files": ["@code"],
              "triggers": ["\\bcatch\\b"], "severity": "medium",
              "check": "Handle or rethrow caught exceptions.", "bad": "catch { }", "good": "catch (E e) { throw new X(e); }",
              "allowed": ["Top-level handlers"] },
            { "id": "std-secret", "rules": ["standard-secret"], "files": ["@code", "*.env"],
              "triggers": ["(?i)password\\s*="], "severity": "high", "check": "Do not commit secrets." },
            { "id": "std-weak-test", "rules": ["standard-tests"], "files": ["@tests"], "triggers": [],
              "severity": "low", "check": "Keep tests deterministic." }
          ],
          "notChecked": [ { "rule": "standard-graph", "reason": "Needs the whole dependency graph." } ]
        }
        """;

    static List<FileDiff> Added(string path, params string[] lines) =>
        GitDiff.Parse($"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -0,0 +1,{lines.Length} @@\n"
            + string.Concat(lines.Select(l => $"+{l}\n")));

    static RuleCatalog Load(string? checks = Checks) => RuleCatalog.Parse(Catalog, checks);

    static IEnumerable<string> Ids(RuleSelection s) => s.Rules.Select(r => r.Id);

    [Fact]
    public void Sends_a_check_only_when_a_trigger_matches_an_added_line_in_scope()
    {
        RuleCatalog catalog = Load();
        Assert.Equal(["std-swallowed"], Ids(catalog.Select(Added("src/A.cs", "try { Run(); }", "catch { }"))));
        Assert.Empty(Ids(catalog.Select(Added("src/A.cs", "Run();"))));
        Assert.Empty(Ids(catalog.Select(Added("src/notes.txt", "catch { }"))));
        Assert.Equal(["std-secret"], Ids(catalog.Select(Added("prod.env", "PASSWORD = hunter2"))));
    }

    [Fact]
    public void Context_lines_do_not_trigger_checks()
    {
        List<FileDiff> files = GitDiff.Parse("""
            diff --git a/src/A.cs b/src/A.cs
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -1,2 +1,3 @@
             catch { }
            +Run();
             End();
            """);
        Assert.Empty(Load().Select(files).Rules);
    }

    [Fact]
    public void Check_without_triggers_is_sent_for_every_file_in_scope()
    {
        Assert.Equal(["std-weak-test"], Ids(Load().Select(Added("tests/CalcTests.cs", "var x = 1;"))));
    }

    [Fact]
    public void Model_check_id_is_reported_under_its_first_catalog_rule()
    {
        List<FileDiff> files = Added("src/A.cs", "catch { }");
        RuleSelection selection = Load().Select(files);
        string Finding(string id) => System.Text.Json.JsonSerializer.Serialize(new
        {
            findings = new[] { new { ruleId = id, severity = "medium", title = "Swallowed", message = "Rethrow",
                filePath = "src/A.cs", startLine = 1, confidence = 0.9 } }
        });
        var byPath = files.ToDictionary(f => f.Path);

        Assert.Equal("standard-catch", Assert.Single(Review.Parse(Finding("std-swallowed"), byPath, 0.5, [], selection.AllowedIds)).RuleId);
        Assert.Equal("standard-catch", Assert.Single(Review.Parse(Finding("STD-Swallowed"), byPath, 0.5, [], selection.AllowedIds)).RuleId);
        Assert.Equal("standard-cause", Assert.Single(Review.Parse(Finding("standard-cause"), byPath, 0.5, [], selection.AllowedIds)).RuleId);
        var rejected = new List<string>();
        Assert.Empty(Review.Parse(Finding("std-secret"), byPath, 0.5, rejected, selection.AllowedIds));
        Assert.Empty(Review.Parse(Finding("std-made-up"), byPath, 0.5, rejected, selection.AllowedIds));
        Assert.Equal(2, rejected.Count);
        Assert.Single(Review.Parse(Finding("null-dereference"), byPath, 0.5, [], selection.AllowedIds));
    }

    [Fact]
    public void Prompt_lists_check_ids_with_examples_and_allowed_cases()
    {
        string prompt = RuleCatalog.BuildPrompt("BASE", Load().Select(Added("src/A.cs", "catch { }")));
        Assert.Contains("[std-swallowed] medium: Handle or rethrow caught exceptions.", prompt);
        Assert.Contains("  Bad: catch { }", prompt);
        Assert.Contains("  Allowed: Top-level handlers", prompt);
        Assert.DoesNotContain("std-secret", prompt);
        Assert.DoesNotContain("standard-catch", prompt);
    }

    [Fact]
    public void Limit_keeps_highest_severity_checks_and_reports_the_rest()
    {
        List<FileDiff> files = [.. Added("src/A.cs", "catch { }", "password = 1"), .. Added("tests/ATests.cs", "x")];
        RuleSelection selection = Load().Select(files, maxRules: 2);
        Assert.Equal(["std-swallowed", "std-secret"], Ids(selection));
        Assert.Equal(["std-weak-test"], selection.Omitted);
        Assert.False(selection.AllowedIds.ContainsKey("std-weak-test"));

        RuleSelection one = Load().Select(files, maxRules: 1);
        Assert.Equal(["std-secret"], Ids(one));
        Assert.Equal(["std-swallowed", "std-weak-test"], one.Omitted);
        Assert.Equal(3, Load().Select(files).Rules.Count);
    }

    [Fact]
    public async Task Review_records_checks_omitted_by_the_limit()
    {
        List<FileDiff> files = [.. Added("src/A.cs", "catch { }", "password = 1")];
        var stub = new StubHandler((_, _) => StubHandler.Chat("""{"findings":[]}"""));
        var provider = new Provider(new HttpClient(stub), new ProviderConfig { Endpoint = "http://x/v1/chat/completions", Model = "m" }, null);
        ReviewResult result = await Review.RunAsync(files, new ReviewConfig { MaxStandardChecksPerRequest = 1 }, provider, "BASE", default, Load());

        Assert.Equal(["std-swallowed"], result.OmittedStandardChecks);
        Assert.Contains("std-secret", stub.Requests[0].Body);
        Assert.DoesNotContain("std-swallowed", stub.Requests[0].Body);
        Assert.Contains("omittedStandardChecks", Report.ToJson(result));
    }

    [Fact]
    public void Ruleset_hash_covers_the_checks()
    {
        Assert.NotEqual(Load(null).Sha256, Load().Sha256);
        Assert.NotEqual(Load().Sha256, Load(Checks.Replace("Do not commit secrets.", "Never commit secrets.")).Sha256);
        Assert.Equal(Load().Sha256, Load().Sha256);
    }

    [Theory]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 2")]
    [InlineData("\"id\": \"std-secret\"", "\"id\": \"secret\"")]
    [InlineData("\"id\": \"std-secret\"", "\"id\": \"std-swallowed\"")]
    [InlineData("\"severity\": \"high\"", "\"severity\": \"urgent\"")]
    [InlineData("\"check\": \"Do not commit secrets.\"", "\"check\": \" \"")]
    [InlineData("\"@code\", \"*.env\"", "\"@missing\"")]
    [InlineData("(?i)password\\\\s*=", "password[")]
    [InlineData("\"rules\": [\"standard-secret\"]", "\"rules\": []")]
    [InlineData("\"rules\": [\"standard-secret\"]", "\"rules\": [\"standard-static\"]")]
    [InlineData("\"rules\": [\"standard-secret\"]", "\"rules\": [\"standard-secret\", \"standard-catch\"]")]
    [InlineData("\"rules\": [\"standard-secret\"]", "\"rules\": [\"standard-unknown\"]")]
    [InlineData("{ \"rule\": \"standard-graph\", \"reason\": \"Needs the whole dependency graph.\" }", "")]
    [InlineData("\"reason\": \"Needs the whole dependency graph.\"", "\"reason\": \"\"")]
    [InlineData("\"tests\": [\"*Tests.cs\"]", "\"tests\": []")]
    public void Rejects_invalid_checks(string from, string to)
    {
        string broken = Checks.Replace(from, to);
        Assert.NotEqual(Checks, broken);
        SmartWizzException e = Assert.Throws<SmartWizzException>(() => Load(broken));
        Assert.Equal(FailureKind.Configuration, e.Kind);
    }

    [Fact]
    public void Repository_checks_cover_the_catalog_and_trigger_on_typical_code()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var catalog = RuleCatalog.Load(Path.Combine(root, "rules", "engineering-rules.json"), Path.Combine(root, "rules", "llm-checks.json"));

        Assert.NotNull(catalog.Checks);
        Assert.Contains("std-swallowed-exception", Ids(catalog.Select(Added("src/A.cs", "catch (Exception) { return 0; }"))));
        Assert.Contains("std-blocking-async", Ids(catalog.Select(Added("src/A.cs", "return client.GetStringAsync(url).Result;"))));
        Assert.Contains("std-committed-secret", Ids(catalog.Select(Added("src/A.cs", "const string password = \"x\";"))));
        Assert.Contains("std-ts-import-alias", Ids(catalog.Select(Added("web/a.ts", "import { User } from \"../../models/User\";"))));
        Assert.DoesNotContain("std-blocking-async", Ids(catalog.Select(Added("web/a.ts", "const r = p.Result;"))));
        Assert.Empty(catalog.Select(Added("README.md", "catch (Exception) { }")).Rules);
        Assert.True(catalog.Select(Added("src/A.cs", "int total = items.Count;")).Rules.Count <= 3);
    }
}
