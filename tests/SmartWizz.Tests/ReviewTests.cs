

namespace SmartWizz.Tests;

public class ReviewTests
{
    static readonly Dictionary<string, FileDiff> Files = GitDiff.Parse(GitDiffTests.Sample).ToDictionary(f => f.Path);

    static string One(string fields) => $$"""{"findings":[{{{fields}}}]}""";

    const string Good = """
        "ruleId":"wrong-operator","severity":"high","title":"t","message":"m",
        "filePath":"src/Calc.cs","startLine":12,"endLine":13,"confidence":0.9
        """;

    static List<Finding> Parse(string reply, List<string>? rejected = null) =>
        Review.Parse(reply, Files, 0.5, rejected ?? []);

    [Theory]
    [InlineData(99, false, false, true)]
    [InlineData(11, false, false, true)]
    [InlineData(12, false, false, true)]
    [InlineData(99, true, false, false)]
    [InlineData(99, false, true, false)]
    public void Source_evidence_requires_unique_added_line(int reported, bool duplicate, bool contextOnly, bool accepted)
    {
        var file = new FileDiff { Path = "src/Calc.cs" };
        file.NewLines.Add(12, "    incorrect();");
        if (!contextOnly) file.AddedLines.Add(12);
        if (duplicate)
        {
            file.NewLines.Add(13, "incorrect();");
            file.AddedLines.Add(13);
        }
        var files = new Dictionary<string, FileDiff> { [file.Path] = file };
        string reply = One(Good.Replace("\"startLine\":12", $"\"startLine\":{reported}")
            .Replace("\"endLine\":13", "\"endLine\":null") +
            ",\"sourceLine\":\"incorrect();\",\"suggestedFix\":\"corrected();\"");
        var rejected = new List<string>();
        List<Finding> findings = Review.Parse(reply, files, 0.5, rejected);
        if (!accepted)
        {
            Assert.Empty(findings);
            Assert.Single(rejected);
            return;
        }
        Finding finding = Assert.Single(findings);
        Assert.Empty(rejected);
        Assert.Equal(12, finding.StartLine);
        Assert.Equal(reported == 12 ? "corrected();" : null, finding.SuggestedFix);
    }

    [Theory]
    [InlineData("first();\n second();", true, false, false)]
    [InlineData("first();\r\n second();", true, false, false)]
    [InlineData("first();\n second();", false, true, false)]
    [InlineData("first();\n second();", false, false, true)]
    [InlineData("first();\n altered();", false, false, false)]
    public void Multiline_evidence_requires_unique_contiguous_source(string evidence, bool accepted, bool duplicate, bool gap)
    {
        var file = new FileDiff { Path = "src/Calc.cs" };
        file.NewLines.Add(12, "    first();");
        file.NewLines.Add(gap ? 14 : 13, "    second();");
        file.AddedLines.Add(12);
        if (duplicate)
        {
            file.NewLines.Add(20, "first();");
            file.NewLines.Add(21, "second();");
            file.AddedLines.Add(20);
        }
        var files = new Dictionary<string, FileDiff> { [file.Path] = file };
        string reply = One(Good.Replace("\"startLine\":12", "\"startLine\":99") +
            ",\"sourceLine\":" + System.Text.Json.JsonSerializer.Serialize(evidence));
        var rejected = new List<string>();
        List<Finding> findings = Review.Parse(reply, files, 0.5, rejected);
        if (accepted)
        {
            Finding finding = Assert.Single(findings);
            Assert.Equal(12, finding.StartLine);
            Assert.Null(finding.EndLine);
            Assert.Empty(rejected);
        }
        else
        {
            Assert.Empty(findings);
            Assert.Contains(duplicate ? "source-evidence-ambiguous" : "source-evidence-no-match", Assert.Single(rejected), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("F1-L12", 12, true)]
    [InlineData("F1-L12", 99, true)]
    [InlineData("F1-L11", 11, false)]
    [InlineData("F1-L99", 12, false)]
    [InlineData("F2-L12", 12, false)]
    [InlineData("", 12, false)]
    [InlineData("not-a-reference", 12, false)]
    public void Source_reference_must_belong_to_added_line_in_reported_file(string reference, int reported, bool accepted)
    {
        var file = new FileDiff { Path = "src/Calc.cs" };
        file.NewLines.Add(11, "context();");
        file.NewLines.Add(12, "incorrect();");
        file.AddedLines.Add(12);
        var other = new FileDiff { Path = "src/Other.cs" };
        other.NewLines.Add(12, "other();");
        other.AddedLines.Add(12);
        var files = new Dictionary<string, FileDiff> { [file.Path] = file, [other.Path] = other };
        Dictionary<string, (string Path, int Line)> references = Review.SourceReferences([file, other]);
        string reply = One(Good.Replace("\"startLine\":12", $"\"startLine\":{reported}")
            .Replace("\"endLine\":13", "\"endLine\":null") +
            ",\"sourceRef\":" + System.Text.Json.JsonSerializer.Serialize(reference) +
            ",\"suggestedFix\":\"corrected();\"");
        var rejected = new List<string>();
        List<Finding> findings = Review.Parse(reply, files, 0.5, rejected, references: references);
        if (!accepted)
        {
            Assert.Empty(findings);
            string expected = reference is "" or "not-a-reference" ? "source-reference-malformed" :
                reference == "F2-L12" ? "source-reference-file-mismatch" : "source-reference-unknown";
            Assert.Contains(expected, Assert.Single(rejected), StringComparison.Ordinal);
            return;
        }
        Finding finding = Assert.Single(findings);
        Assert.Equal(12, finding.StartLine);
        Assert.Equal(reported == 12 ? "corrected();" : null, finding.SuggestedFix);
        Assert.Empty(rejected);
    }

    [Fact]
    public void Parses_valid_finding_inside_code_fence()
    {
        Finding f = Assert.Single(Parse("Here you go:\n```json\n" + One(Good) + "\n```"));
        Assert.Equal(Severity.High, f.Severity);
        Assert.Equal((12, 13), (f.StartLine, f.EndLine!.Value));
        Assert.Equal(16, f.Fingerprint.Length);
    }

    [Fact]
    public void Suggested_fix_keeps_indentation_and_drops_control_characters()
    {
        Finding f = Assert.Single(Parse(One(Good + """,  "suggestedFix":"    a();\n\tb();\u001b[31m\n" """)));
        Assert.Equal("    a();\n    b();[31m", f.SuggestedFix);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"   \"")]
    [InlineData("\"\\u0007\"")]
    public void Unusable_suggested_fix_keeps_the_finding(string value) =>
        Assert.Null(Assert.Single(Parse(One(Good + ",\"suggestedFix\":" + value))).SuggestedFix);

    [Fact]
    public void Empty_findings_is_fine() => Assert.Empty(Parse("""{"findings": []}"""));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ broken")]
    [InlineData("""{"results": []}""")]
    [InlineData("""{"findings": {}}""")]
    public void Invalid_model_output_is_an_execution_failure(string reply) =>
        Assert.Throws<SmartWizzException>(() => Parse(reply));

    [Theory]
    [InlineData("\"filePath\":\"src/Calc.cs\"", "\"filePath\":\"src/Other.cs\"")] // file not in diff
    [InlineData("\"filePath\":\"src/Calc.cs\"", "\"filePath\":\"old.txt\"")]      // not in this batch's lines
    [InlineData("\"startLine\":12", "\"startLine\":99")]                          // line outside diff
    [InlineData("\"startLine\":12", "\"startLine\":11")]                          // unchanged context line
    [InlineData("\"endLine\":13", "\"endLine\":15")]                              // end past the hunk
    [InlineData("\"startLine\":12", "\"startLine\":-1")]
    [InlineData("\"endLine\":13", "\"endLine\":11")]                              // end before start
    [InlineData("\"severity\":\"high\"", "\"severity\":\"urgent\"")]
    [InlineData("\"severity\":\"high\"", "\"severity\":\"1\"")]
    [InlineData("\"confidence\":0.9", "\"confidence\":1.5")]
    [InlineData("\"confidence\":0.9", "\"confidence\":0.2")]                      // below minConfidence
    [InlineData("\"title\":\"t\"", "\"title\":\"  \"")]
    public void Invalid_findings_are_rejected_not_fatal(string from, string to)
    {
        var rejected = new List<string>();
        Assert.Empty(Parse(One(Good.Replace(from, to)), rejected));
        Assert.Single(rejected);
    }

    [Theory]
    [InlineData("/src/Calc.cs")]
    [InlineData("./src/Calc.cs")]
    [InlineData("b/src/Calc.cs")]
    [InlineData("src\\\\Calc.cs")]
    public void Path_variants_are_normalized(string path)
    {
        Finding f = Assert.Single(Parse(One(Good.Replace("\"src/Calc.cs\"", $"\"{path}\""))));
        Assert.Equal("src/Calc.cs", f.FilePath);
    }

    [Fact]
    public void Range_may_extend_over_context_but_not_across_a_gap_between_hunks()
    {
        var files = GitDiff.Parse("""
            diff --git a/a.cs b/a.cs
            --- a/a.cs
            +++ b/a.cs
            @@ -1,2 +1,3 @@
             one
            +two
             three
            @@ -20,2 +21,3 @@
             x
            +y
             z

            """).ToDictionary(f => f.Path);
        string Finding(int start, int end) =>
            $$"""{"findings":[{"ruleId":"r","severity":"low","title":"t","message":"m","filePath":"a.cs","startLine":{{start}},"endLine":{{end}},"confidence":0.9}]}""";

        Assert.Single(Review.Parse(Finding(2, 3), files, 0.5, []));    // added line + following context
        var rejected = new List<string>();
        Assert.Empty(Review.Parse(Finding(2, 22), files, 0.5, rejected)); // spans lines not shown
        Assert.Contains("invalid line range", Assert.Single(rejected));
    }

    [Fact]
    public void Fingerprint_ignores_whitespace_and_line_number_but_not_rule()
    {
        string a = Review.Fingerprint("Null-Deref", "a.cs", "  x.Foo();");
        Assert.Equal(a, Review.Fingerprint("null-deref", "a.cs", "x.Foo();\r"));
        Assert.NotEqual(a, Review.Fingerprint("sql-injection", "a.cs", "x.Foo();"));
        Assert.NotEqual(a, Review.Fingerprint("null-deref", "b.cs", "x.Foo();"));
    }

    [Fact]
    public void Dedupe_keeps_most_severe_and_sorts()
    {
        Finding F(Severity s, double c, string fp, int line = 1) => new("r", s, "t", "m", "a.cs", line, null, c, fp);
        List<Finding> result = Review.Dedupe([F(Severity.Low, 0.9, "x"), F(Severity.High, 0.6, "x"), F(Severity.Critical, 0.7, "y", 5)]);
        Assert.Equal([("y", Severity.Critical), ("x", Severity.High)], result.Select(f => (f.Fingerprint, f.Severity)));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(Severity.Critical, 0)]
    [InlineData(Severity.High, 1)]
    [InlineData(Severity.Low, 1)]
    public void Exit_code_follows_threshold(Severity? failOn, int expected)
    {
        Finding[] findings = new[] { new Finding("r", Severity.High, "t", "m", "a.cs", 1, null, 1, "x") };
        Assert.Equal(expected, Review.ExitCode(findings, failOn));
    }

    [Fact]
    public void Exit_code_is_zero_without_findings() => Assert.Equal(0, Review.ExitCode([], Severity.Low));

    [Fact]
    public async Task RunAsync_skips_unreviewable_files_and_batches_the_rest()
    {
        var stub = new StubHandler((_, _) => StubHandler.Chat(One(Good + ",\"sourceRef\":\"F1-L12\"")));
        var provider = new Provider(new HttpClient(stub), new ProviderConfig { Endpoint = "http://x/v1/chat/completions", Model = "m" }, null);

        ReviewResult result = await Review.RunAsync(GitDiff.Parse(GitDiffTests.Sample), new ReviewConfig(), provider, TestPrompts.Default, default);

        Assert.Single(stub.Requests);
        Assert.Equal(1, result.ReviewedFiles);
        Finding finding = Assert.Single(result.Findings);
        ReviewRequestTiming timing = Assert.Single(result.RequestTimings);
        Assert.Equal(1, timing.Request);
        Assert.Equal("m", timing.Model);
        Assert.Equal(1, timing.Files);
        Assert.Equal(1, timing.AcceptedFindings);
        Assert.True(timing.ElapsedMilliseconds >= 0);
        Assert.True(result.ReviewElapsedMilliseconds >= timing.ElapsedMilliseconds);
        Assert.Equal(timing.Request, result.FindingRequests[finding.Fingerprint]);
        Assert.Equal(["deleted", "binary", "excluded"], result.SkippedFiles.Select(s => s.Reason));
        Assert.Contains("BEGIN-DIFF-", stub.Requests[0].Body);
        Assert.Contains("- web/package-lock.json\\n", stub.Requests[0].Body); // listed for context (JSON-escaped newline)...
        Assert.DoesNotContain("File: web/package-lock.json", stub.Requests[0].Body); // ...but not sent
    }

    [Fact]
    public void User_message_nonce_differs_per_call() =>
        Assert.NotEqual(Review.BuildDiff([], [], Review.NewNonce()), Review.BuildDiff([], [], Review.NewNonce()));

    [Fact]
    public void Staged_mode_requires_no_base_and_rejects_pr_or_checkpoint_options()
    {
        var staged = Options.Parse(["--staged", "--json", "review.json", "--fail-on", "high"]);
        Assert.True(staged.Staged);
        Assert.Null(staged.Base);
        Assert.Equal(Severity.High, staged.FailOn);

        string[][] incompatible =
        [
            ["--base", "main"], ["--pr", "12"], ["--head", "other"],
            ["--since", new string('a', 40)], ["--state-out", "state.json"],
            ["--full-review"], ["--publish"], ["--publish-github"],
        ];
        foreach (string[] other in incompatible)
            Assert.Throws<SmartWizzException>(() => Options.Parse(["--staged", .. other]));
    }

    [Fact]
    public void Check_needs_no_other_options_and_rejects_pr_options()
    {
        var check = Options.Parse(["check"]);
        Assert.True(check.Check);
        Assert.False(check.Staged);
        Assert.True(Options.Parse(["check", "--staged"]).Staged);

        Assert.Throws<SmartWizzException>(() => Options.Parse(["check", "--base", "main"]));
        Assert.Throws<SmartWizzException>(() => Options.Parse(["check", "--publish"]));
        Assert.Throws<SmartWizzException>(() => Options.Parse(["--json", "out.json", "check"]));
    }

    [Fact]
    public void Options_require_base_and_validate_severity()
    {
        Assert.Throws<SmartWizzException>(() => Options.Parse([]));
        Assert.Throws<SmartWizzException>(() => Options.Parse(["--base", "main", "--fail-on", "urgent"]));
        Assert.Throws<SmartWizzException>(() => Options.Parse(["--base"]));
        var o = Options.Parse(["--base", "origin/main", "--fail-on", "HIGH", "--publish"]);
        Assert.Equal(("origin/main", Severity.High, true), (o.Base, o.FailOn, o.Publish));
    }
}

public class SanitizeTests
{
    [Fact]
    public void Control_characters_are_stripped_from_model_text()
    {
        var files = GitDiff.Parse(GitDiffTests.Sample).ToDictionary(f => f.Path);
        const string reply = """
            {"findings":[{"ruleId":"r\u0007","severity":"low","title":"a\u001b[31mred\r\nb","message":"x\r\ny\u0000",
             "filePath":"src/Calc.cs","startLine":12,"confidence":0.9}]}
            """;
        Finding f = Assert.Single(Review.Parse(reply, files, 0.5, []));
        Assert.Equal(("r", "a[31mredb", "x\ny"), (f.RuleId, f.Title, f.Message));
    }
}
