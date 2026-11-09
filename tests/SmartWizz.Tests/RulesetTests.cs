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

    static Provider Provider(StubHandler stub, bool jsonSchema = false) =>
        new(new HttpClient(stub), new ProviderConfig { Endpoint = "http://x/v1/chat/completions", Model = "m", JsonSchema = jsonSchema }, null);

    static readonly ReviewPrompts Prompts = new("SHARED-SYSTEM", "GENERAL-TASK", "RECHECK", "STANDARDS-TASK");

    static (string System, string User) Messages(string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        System.Text.Json.JsonElement messages = doc.RootElement.GetProperty("messages");
        return (messages[0].GetProperty("content").GetString()!, messages[1].GetProperty("content").GetString()!);
    }

    // Everything up to and including the END-DIFF marker line: the untrusted part, shared across passes.
    static string DiffPrefix(string user)
    {
        string nonce = System.Text.RegularExpressions.Regex.Match(user, "BEGIN-DIFF-([0-9A-F]{16})\n").Groups[1].Value;
        Assert.NotEmpty(nonce);
        string marker = $"\nEND-DIFF-{nonce}\n";
        return user[..(user.IndexOf(marker, StringComparison.Ordinal) + marker.Length)];
    }

    // Long enough that the test file is over half the batch, so its follow-up pass keeps the full, shared diff.
    static readonly string[] LongTest = ["Assert.Equal(expectedTotal, calculator.Sum(firstValue, secondValue));",
        "Assert.Equal(expectedCount, calculator.Count(firstValue, secondValue));"];

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
        List<FileDiff> files = [.. Added("src/A.cs", "catch { }", "password = 1"), .. Added("tests/ATests.cs", LongTest)];
        int call = 0;
        var stub = new StubHandler((_, _) => StubHandler.Chat(++call switch
        {
            1 => Finding("std-secret", line: 2),
            // A follow-up pass may only report its own checks: the bug finding is rejected, the check is kept.
            _ => """{"findings":[""" + Finding("std-weak-test", "tests/ATests.cs")[13..^2] + "," + Finding("null-dereference")[13..^2] + "]}",
        }));
        ReviewResult result = await Review.RunAsync(files, new ReviewConfig { MaxChecksPerRequest = 2 }, Provider(stub), Prompts, default, Rules);

        Assert.Equal(2, result.Requests);
        Assert.Equal(2, stub.Requests.Count);
        (string System, string User)[] requests = [.. stub.Requests.Select(r => Messages(r.Body!))];
        (string firstTask, string secondTask) = (requests[0].User[DiffPrefix(requests[0].User).Length..], requests[1].User[DiffPrefix(requests[1].User).Length..]);

        // Prefix-cache friendly: one system prompt, then the identical diff block (same nonce); only the task differs.
        Assert.All(requests, r => Assert.Equal("SHARED-SYSTEM", r.System));
        Assert.Equal(DiffPrefix(requests[0].User), DiffPrefix(requests[1].User));
        Assert.Contains("File: src/A.cs", DiffPrefix(requests[1].User));

        // Checks (trusted) only after the end marker, never in the untrusted block or the system prompt.
        Assert.All(requests, r => Assert.DoesNotContain("[std-", DiffPrefix(r.User)));
        Assert.All(requests, r => Assert.DoesNotContain("std-", r.System));
        Assert.Contains("GENERAL-TASK", firstTask);
        Assert.Contains("[std-secret]", firstTask);
        Assert.Contains("[std-swallowed]", firstTask);
        Assert.DoesNotContain("std-weak-test", firstTask);
        Assert.Contains("STANDARDS-TASK", secondTask);
        Assert.DoesNotContain("GENERAL-TASK", secondTask);
        Assert.Contains("[std-weak-test]", secondTask);
        Assert.DoesNotContain("std-secret", secondTask);

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
    public async Task Follow_up_pass_sends_only_the_files_its_checks_apply_to()
    {
        // std-weak-test (pass 2) applies only to the small test file; the large source file is left out.
        List<FileDiff> files =
        [
            .. Added("src/A.cs", ["catch { }", "password = 1", .. Enumerable.Range(0, 20).Select(i => $"int value{i} = Compute({i});")]),
            .. Added("tests/BTests.cs", "x"),
        ];
        int call = 0;
        var stub = new StubHandler((_, _) => StubHandler.Chat(++call switch
        {
            1 => """{"findings":[]}""",
            // A finding on a file this pass did not send is rejected.
            _ => """{"findings":[""" + Finding("std-weak-test", "tests/BTests.cs")[13..^2] + "," + Finding("std-weak-test", "src/A.cs")[13..^2] + "]}",
        }));
        ReviewResult result = await Review.RunAsync(files, new ReviewConfig { MaxChecksPerRequest = 2 }, Provider(stub), Prompts, default, Rules);

        Assert.Equal(2, stub.Requests.Count);
        (string System, string User)[] requests = [.. stub.Requests.Select(r => Messages(r.Body!))];
        Assert.Contains("File: src/A.cs", requests[0].User);
        Assert.Contains("File: tests/BTests.cs", requests[0].User);
        Assert.Contains("File: tests/BTests.cs", requests[1].User);
        Assert.DoesNotContain("src/A.cs", requests[1].User);
        Assert.DoesNotContain("src/A.cs", stub.Requests[1].Body);

        Finding kept = Assert.Single(result.Findings);
        Assert.Equal(("std-weak-test", "tests/BTests.cs"), (kept.RuleId, kept.FilePath));
        Assert.Contains(result.Rejected, r => r.Contains("src/A.cs: file not in reviewed diff"));

        // The report counts what was actually sent.
        Assert.True(requests[1].User.Length < requests[0].User.Length / 2);
        Assert.Equal(requests.Sum(r => (long)r.User.Length), result.UserPromptCharacters);
        Assert.Equal(requests.Sum(r => (long)r.System.Length), result.SystemPromptCharacters);
    }

    [Fact]
    public async Task Json_schema_is_sent_only_when_enabled_and_follow_ups_enumerate_their_check_ids()
    {
        List<FileDiff> files = [.. Added("src/A.cs", "catch { }", "password = 1"), .. Added("tests/ATests.cs", LongTest)];
        var stub = new StubHandler((_, _) => StubHandler.Chat("""{"findings":[]}"""));
        await Review.RunAsync(files, new ReviewConfig { MaxChecksPerRequest = 2 }, Provider(stub, jsonSchema: true), Prompts, default, Rules);
        await Review.RunAsync(files, new ReviewConfig { MaxChecksPerRequest = 2 }, Provider(stub), Prompts, default, Rules);

        Assert.Equal(4, stub.Requests.Count);
        System.Text.Json.Nodes.JsonNode Format(int i) => System.Text.Json.Nodes.JsonNode.Parse(stub.Requests[i].Body!)!["response_format"]!;
        System.Text.Json.Nodes.JsonNode Finding(int i) => Format(i)["json_schema"]!["schema"]!["properties"]!["findings"]!["items"]!["properties"]!;
        string[] Enum(System.Text.Json.Nodes.JsonNode? node) => [.. node!.AsArray().Select(v => v!.GetValue<string>())];

        Assert.Equal("json_schema", Format(0)["type"]!.GetValue<string>());
        Assert.Equal("findings", Format(0)["json_schema"]!["name"]!.GetValue<string>());
        Assert.True(Format(0)["json_schema"]!["strict"]!.GetValue<bool>());
        // Pass 1 keeps ruleId free (bug categories); the follow-up pass allows exactly the checks it was sent.
        Assert.Null(Finding(0)["ruleId"]!["enum"]);
        Assert.Equal(["std-weak-test"], Enum(Finding(1)["ruleId"]!["enum"]));
        Assert.Equal(["low", "medium", "high", "critical"], Enum(Finding(0)["severity"]!["enum"]));
        Assert.Equal(["src/A.cs", "tests/ATests.cs"], Enum(Finding(0)["filePath"]!["enum"]));

        Assert.DoesNotContain("response_format", stub.Requests[2].Body);
        Assert.DoesNotContain("response_format", stub.Requests[3].Body);
    }

    [Fact]
    public async Task Report_counts_requests_and_kept_findings_per_check()
    {
        List<FileDiff> first = Added("src/A.cs", "catch { }", "password = 1");
        List<FileDiff> second = Added("src/B.cs", "catch { }");
        int call = 0;
        var stub = new StubHandler((_, _) => StubHandler.Chat(++call switch
        {
            // std-weak-test was not sent, so it is rejected and must not count.
            1 => """{"findings":[""" + Finding("std-secret", line: 2)[13..^2] + "," + Finding("std-weak-test")[13..^2] + "]}",
            // The same finding twice is deduplicated and counts once.
            2 => """{"findings":[""" + Finding("std-swallowed")[13..^2] + "," + Finding("std-swallowed")[13..^2] + "]}",
            _ => Finding("std-swallowed", "src/B.cs"),
        }));
        ReviewConfig cfg = new() { MaxChecksPerRequest = 1, MaxCharsPerRequest = first[0].Annotated.Length };
        ReviewResult result = await Review.RunAsync([.. first, .. second], cfg, Provider(stub), Prompts, default, Rules);

        // Batch A: secret, then swallowed. Batch B: swallowed. std-weak-test applies to neither.
        Assert.Equal(3, result.Requests);
        using var report = System.Text.Json.JsonDocument.Parse(Report.ToJson(result));
        System.Text.Json.JsonElement checks = report.RootElement.GetProperty("checks");
        (int, int) Counts(string id) => (checks.GetProperty(id).GetProperty("sent").GetInt32(), checks.GetProperty(id).GetProperty("findings").GetInt32());
        Assert.Equal((1, 1), Counts("std-secret"));
        Assert.Equal((2, 2), Counts("std-swallowed"));
        Assert.Equal((0, 0), Counts("std-weak-test"));

        Assert.DoesNotContain("\"checks\"", Report.ToJson(new ReviewResult()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Format_prompt_follows_suggest_fixes_and_states_the_confidence_floor(bool suggestFixes)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        string template = File.ReadAllText(Path.Combine(root, "prompts", "review-format.md"));

        string prompt = Review.FormatPrompt(template, new ReviewConfig { SuggestFixes = suggestFixes, MinConfidence = 0.65 });

        Assert.Equal(suggestFixes, prompt.Contains("suggestedFix"));
        Assert.Contains("confidence ≥ 0.65.", prompt);
        Assert.Contains("\"confidence\": 0.0" + (suggestFixes ? ", \"suggestedFix\"" : "}]}"), prompt);
        Assert.DoesNotContain("[[fix:", prompt);
        Assert.DoesNotContain("]]", prompt);
        Assert.DoesNotContain("{minConfidence}", prompt);
    }

    [Fact]
    public async Task Without_suggested_fixes_the_schema_leaves_the_field_out_and_the_scope_changes()
    {
        var stub = new StubHandler((_, _) => StubHandler.Chat("""{"findings":[]}"""));
        await Review.RunAsync(Added("src/A.cs", "x"), new ReviewConfig { SuggestFixes = false }, Provider(stub, jsonSchema: true), Prompts, default);
        await Review.RunAsync(Added("src/A.cs", "x"), new ReviewConfig(), Provider(stub, jsonSchema: true), Prompts, default);

        Assert.DoesNotContain("suggestedFix", stub.Requests[0].Body);
        Assert.Contains("suggestedFix", stub.Requests[1].Body);
        // Program hashes the serialized config into the review scope.
        Assert.NotEqual(System.Text.Json.JsonSerializer.Serialize(new Config()),
            System.Text.Json.JsonSerializer.Serialize(new Config { Review = new ReviewConfig { SuggestFixes = false } }));
    }

    [Fact]
    public void Schema_shaped_replies_with_null_optional_fields_parse()
    {
        var byPath = Added("src/A.cs", "catch { }").ToDictionary(f => f.Path);
        const string reply = """
            {"findings":[{"ruleId":"std-swallowed","severity":"medium","title":"t","message":"m","filePath":"src/A.cs",
             "startLine":1,"endLine":null,"confidence":0.9,"suggestedFix":null}]}
            """;
        Finding f = Assert.Single(Review.Parse(reply, byPath, 0.5, [], ["std-swallowed"], standardsOnly: true));
        Assert.Equal((null, null), (f.EndLine, f.SuggestedFix));
    }

    [Fact]
    public async Task Without_a_limit_or_ruleset_each_batch_is_one_request()
    {
        List<FileDiff> files = [.. Added("src/A.cs", "catch { }", "password = 1"), .. Added("tests/ATests.cs", "x")];
        var stub = new StubHandler((_, _) => StubHandler.Chat("""{"findings":[]}"""));
        Assert.Equal(1, (await Review.RunAsync(files, new ReviewConfig(), Provider(stub), Prompts, default, Rules)).Requests);
        ReviewResult plain = await Review.RunAsync(files, new ReviewConfig(), Provider(stub), Prompts, default);
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
