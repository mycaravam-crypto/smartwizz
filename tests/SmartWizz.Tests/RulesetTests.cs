namespace SmartWizz.Tests;

public sealed class RulesetTests
{
    const string Sample = """
        {
          "schemaVersion": 1,
          "name": "Sample Guide",
          "version": "2.0.0",
          "fileGroups": { "code": ["*.cs", "*.ts"], "tests": ["*Tests.cs"] },
          "checks": [
            { "id": "std-swallowed", "severity": "medium", "files": ["@code"], "triggers": ["\\bcatch\\b"],
              "check": "Handle or rethrow caught exceptions.", "bad": "catch { }", "good": "catch (E e) { throw new X(e); }",
              "allowed": ["Top-level handlers"], "refs": ["4.1"] },
            { "id": "std-secret", "severity": "high", "files": ["@code", "*.env"], "triggers": ["(?i)password\\s*="],
              "check": "Do not commit secrets." },
            { "id": "std-weak-test", "severity": "low", "files": ["@tests"], "triggers": [],
              "check": "Keep tests deterministic." }
          ],
          "notChecked": [ { "ref": "5.1", "reason": "Needs the whole dependency graph." } ]
        }
        """;

    static readonly Ruleset Rules = Ruleset.Parse(Sample);

    static List<FileDiff> Added(string path, params string[] lines) =>
        GitDiff.Parse($"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -0,0 +1,{lines.Length} @@\n"
            + string.Concat(lines.Select(l => $"+{l}\n")));

    static IEnumerable<string> Ids(IEnumerable<Ruleset.Check> checks) => checks.Select(c => c.Id);

    static string Finding(string id, string path = "src/A.cs", int line = 1) => System.Text.Json.JsonSerializer.Serialize(new
    {
        findings = new[] { new { ruleId = id, severity = "medium", title = "Issue", message = "Fix it",
            filePath = path, startLine = line, confidence = 0.9 } }
    });

    static Provider Provider(StubHandler stub) =>
        new(new HttpClient(stub), new ProviderConfig { Endpoint = "http://x/v1/chat/completions", Model = "m" }, null);

    [Fact]
    public void Loads_metadata_and_hashes_the_whole_file()
    {
        Assert.Equal("Sample Guide", Rules.Name);
        Assert.Equal("2.0.0", Rules.Version);
        Assert.Equal(64, Rules.Sha256.Length);
        Assert.Equal(Rules.Sha256, Ruleset.Parse(Sample).Sha256);
        Assert.NotEqual(Rules.Sha256, Ruleset.Parse(Sample.Replace("Do not commit secrets.", "Never commit secrets.")).Sha256);
    }

    [Fact]
    public void Sends_a_check_only_when_a_trigger_matches_an_added_line_in_scope()
    {
        Assert.Equal(["std-swallowed"], Ids(Rules.Select(Added("src/A.cs", "try { Run(); }", "catch { }"))));
        Assert.Empty(Rules.Select(Added("src/A.cs", "Run();")));
        Assert.Empty(Rules.Select(Added("src/notes.txt", "catch { }")));
        Assert.Equal(["std-secret"], Ids(Rules.Select(Added("prod.env", "PASSWORD = hunter2"))));
        Assert.Equal(["std-weak-test"], Ids(Rules.Select(Added("tests/CalcTests.cs", "var x = 1;"))));
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
        Assert.Empty(Rules.Select(files));
    }

    [Fact]
    public void Selection_puts_highest_severity_first_and_passes_split_by_the_limit()
    {
        List<FileDiff> files = [.. Added("src/A.cs", "catch { }", "password = 1"), .. Added("tests/ATests.cs", "x")];
        List<Ruleset.Check> applicable = Rules.Select(files);
        Assert.Equal(["std-secret", "std-swallowed", "std-weak-test"], Ids(applicable));

        Assert.Single(Ruleset.Passes(applicable, 0));
        Assert.Single(Ruleset.Passes(applicable, 3));
        Assert.Equal([["std-secret", "std-swallowed"], ["std-weak-test"]], Ruleset.Passes(applicable, 2).Select(p => Ids(p).ToArray()));
        Assert.Single(Ruleset.Passes([], 2));
    }

    [Fact]
    public void Prompt_lists_checks_with_examples_but_never_guide_refs()
    {
        string prompt = Ruleset.BuildPrompt("BASE", Rules.Select(Added("src/A.cs", "catch { }")), generalReview: true);
        Assert.StartsWith("BASE", prompt);
        Assert.Contains("Also report added code", prompt);
        Assert.Contains("[std-swallowed] medium: Handle or rethrow caught exceptions.", prompt);
        Assert.Contains("  Bad: catch { }", prompt);
        Assert.Contains("  Allowed: Top-level handlers", prompt);
        Assert.DoesNotContain("std-secret", prompt);
        Assert.DoesNotContain("4.1", prompt);

        string followUp = Ruleset.BuildPrompt("STANDARDS", Rules.Select(Added("src/A.cs", "catch { }")), generalReview: false);
        Assert.DoesNotContain("Also report", followUp);
        Assert.DoesNotContain("Bugs and security issues take precedence", followUp);

        Assert.Contains("Do not emit std-*", Ruleset.BuildPrompt("BASE", [], generalReview: true));
    }

    [Fact]
    public void Only_checks_sent_in_the_request_are_accepted()
    {
        List<FileDiff> files = Added("src/A.cs", "catch { }");
        var byPath = files.ToDictionary(f => f.Path);
        string[] ids = ["std-swallowed"];

        Assert.Equal("std-swallowed", Assert.Single(Review.Parse(Finding("STD-Swallowed"), byPath, 0.5, [], ids)).RuleId);
        Assert.Single(Review.Parse(Finding("null-dereference"), byPath, 0.5, [], ids));
        var rejected = new List<string>();
        Assert.Empty(Review.Parse(Finding("std-secret"), byPath, 0.5, rejected, ids));
        Assert.Empty(Review.Parse(Finding("std-made-up"), byPath, 0.5, rejected, ids));
        Assert.Empty(Review.Parse(Finding("null-dereference"), byPath, 0.5, rejected, ids, standardsOnly: true));
        Assert.Equal(3, rejected.Count);
        Assert.All(rejected, r => Assert.Contains("unknown or inapplicable", r));
    }

    [Fact]
    public async Task Checks_over_the_limit_run_in_extra_standards_only_passes()
    {
        List<FileDiff> files = [.. Added("src/A.cs", "catch { }", "password = 1"), .. Added("tests/ATests.cs", "x")];
        int call = 0;
        var stub = new StubHandler((_, _) => StubHandler.Chat(++call switch
        {
            1 => Finding("std-secret", line: 2),
            // A follow-up pass may only report its own checks: the bug finding is rejected, the check is kept.
            _ => """{"findings":[""" + Finding("std-weak-test", "tests/ATests.cs")[13..^2] + "," + Finding("null-dereference")[13..^2] + "]}",
        }));
        ReviewResult result = await Review.RunAsync(files, new ReviewConfig { MaxChecksPerRequest = 2 }, Provider(stub),
            "BASE", default, Rules, "STANDARDS");

        Assert.Equal(2, result.Requests);
        Assert.Equal(2, stub.Requests.Count);
        string first = stub.Requests[0].Body!, second = stub.Requests[1].Body!;
        Assert.Contains("BASE", first);
        Assert.Contains("std-secret", first);
        Assert.Contains("std-swallowed", first);
        Assert.DoesNotContain("std-weak-test", first);
        Assert.Contains("STANDARDS", second);
        Assert.DoesNotContain("BASE", second);
        Assert.Contains("std-weak-test", second);
        Assert.DoesNotContain("std-secret", second);

        Assert.Equal(["std-secret", "std-weak-test"], result.Findings.Select(f => f.RuleId).Order());
        Assert.Contains(result.Rejected, r => r.Contains("null-dereference"));
        Assert.Equal("Sample Guide", result.RulesetName);
        Assert.Equal("2.0.0", result.RulesetVersion);
        Assert.Equal(Rules.Sha256, result.RulesetSha256);
        string json = Report.ToJson(result);
        Assert.Contains("\"rulesetName\"", json);
        Assert.Contains("\"requests\": 2", json);
    }

    [Fact]
    public async Task Without_a_limit_or_ruleset_each_batch_is_one_request()
    {
        List<FileDiff> files = [.. Added("src/A.cs", "catch { }", "password = 1"), .. Added("tests/ATests.cs", "x")];
        var stub = new StubHandler((_, _) => StubHandler.Chat("""{"findings":[]}"""));
        Assert.Equal(1, (await Review.RunAsync(files, new ReviewConfig(), Provider(stub), "BASE", default, Rules, "STANDARDS")).Requests);
        ReviewResult plain = await Review.RunAsync(files, new ReviewConfig(), Provider(stub), "BASE", default);
        Assert.Equal(1, plain.Requests);
        Assert.Null(plain.RulesetName);
        Assert.DoesNotContain("Standard checks", stub.Requests[1].Body);
    }

    [Theory]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 2")]
    [InlineData("\"name\": \"Sample Guide\"", "\"name\": \"\"")]
    [InlineData("\"version\": \"2.0.0\"", "\"version\": \" \"")]
    [InlineData("\"id\": \"std-secret\"", "\"id\": \"secret\"")]
    [InlineData("\"id\": \"std-secret\"", "\"id\": \"STD-Swallowed\"")]
    [InlineData("\"severity\": \"high\"", "\"severity\": \"urgent\"")]
    [InlineData("\"check\": \"Do not commit secrets.\"", "\"check\": \" \"")]
    [InlineData("\"files\": [\"@code\", \"*.env\"]", "\"files\": []")]
    [InlineData("\"@code\", \"*.env\"", "\"@missing\"")]
    [InlineData("(?i)password\\\\s*=", "password[")]
    [InlineData("\"tests\": [\"*Tests.cs\"]", "\"tests\": []")]
    [InlineData("\"reason\": \"Needs the whole dependency graph.\"", "\"reason\": \"\"")]
    public void Rejects_invalid_rulesets(string from, string to)
    {
        string broken = Sample.Replace(from, to);
        Assert.NotEqual(Sample, broken);
        Assert.Equal(FailureKind.Configuration, Assert.Throws<SmartWizzException>(() => Ruleset.Parse(broken)).Kind);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "version": "1", "checks": [] }""")]
    public void Rejects_unusable_files(string json)
    {
        Assert.Throws<SmartWizzException>(() => Ruleset.Parse(json));
    }

    [Fact]
    public void Repository_ruleset_loads_and_triggers_on_typical_violations()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var ruleset = Ruleset.Load(Path.Combine(root, "rulesets", "engineering-standard.json"));

        Assert.Contains("std-swallowed-exception", Ids(ruleset.Select(Added("src/A.cs", "catch (Exception) { return 0; }"))));
        Assert.Contains("std-blocking-async", Ids(ruleset.Select(Added("src/A.cs", "return client.GetStringAsync(url).Result;"))));
        Assert.Contains("std-committed-secret", Ids(ruleset.Select(Added("src/A.cs", "const string password = \"x\";"))));
        Assert.Contains("std-ts-import-alias", Ids(ruleset.Select(Added("web/a.ts", "import { User } from \"../../models/User\";"))));
        Assert.DoesNotContain("std-blocking-async", Ids(ruleset.Select(Added("web/a.ts", "const r = p.Result;"))));
        Assert.Empty(ruleset.Select(Added("README.md", "catch (Exception) { }")));
        Assert.True(ruleset.Select(Added("src/A.cs", "int total = items.Count;")).Count <= 3);
    }
}
