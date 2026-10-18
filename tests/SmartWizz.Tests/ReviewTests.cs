using SmartWizz;

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

    [Fact]
    public void Parses_valid_finding_inside_code_fence()
    {
        var f = Assert.Single(Parse("Here you go:\n```json\n" + One(Good) + "\n```"));
        Assert.Equal(Severity.High, f.Severity);
        Assert.Equal((12, 13), (f.StartLine, f.EndLine!.Value));
        Assert.Equal(16, f.Fingerprint.Length);
    }

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

    [Fact]
    public void Leading_slash_in_path_is_accepted() =>
        Assert.Single(Parse(One(Good.Replace("\"src/Calc.cs\"", "\"/src/Calc.cs\""))));

    [Fact]
    public void Fingerprint_ignores_whitespace_and_line_number_but_not_rule()
    {
        var a = Review.Fingerprint("Null-Deref", "a.cs", "  x.Foo();");
        Assert.Equal(a, Review.Fingerprint("null-deref", "a.cs", "x.Foo();\r"));
        Assert.NotEqual(a, Review.Fingerprint("sql-injection", "a.cs", "x.Foo();"));
        Assert.NotEqual(a, Review.Fingerprint("null-deref", "b.cs", "x.Foo();"));
    }

    [Fact]
    public void Dedupe_keeps_most_severe_and_sorts()
    {
        Finding F(Severity s, double c, string fp, int line = 1) => new("r", s, "t", "m", "a.cs", line, null, c, fp);
        var result = Review.Dedupe([F(Severity.Low, 0.9, "x"), F(Severity.High, 0.6, "x"), F(Severity.Critical, 0.7, "y", 5)]);
        Assert.Equal([("y", Severity.Critical), ("x", Severity.High)], result.Select(f => (f.Fingerprint, f.Severity)));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(Severity.Critical, 0)]
    [InlineData(Severity.High, 1)]
    [InlineData(Severity.Low, 1)]
    public void Exit_code_follows_threshold(Severity? failOn, int expected)
    {
        var findings = new[] { new Finding("r", Severity.High, "t", "m", "a.cs", 1, null, 1, "x") };
        Assert.Equal(expected, Review.ExitCode(findings, failOn));
    }

    [Fact]
    public void Exit_code_is_zero_without_findings() => Assert.Equal(0, Review.ExitCode([], Severity.Low));

    [Fact]
    public async Task RunAsync_skips_unreviewable_files_and_batches_the_rest()
    {
        var stub = new StubHandler((_, _) => StubHandler.Chat(One(Good)));
        var provider = new Provider(new HttpClient(stub), new ProviderConfig { Endpoint = "http://x/v1/chat/completions", Model = "m" }, null);

        var result = await Review.RunAsync(GitDiff.Parse(GitDiffTests.Sample), new ReviewConfig(), provider, "sys", default);

        Assert.Single(stub.Requests);
        Assert.Equal(1, result.ReviewedFiles);
        Assert.Single(result.Findings);
        Assert.Equal(["deleted", "binary", "excluded"], result.SkippedFiles.Select(s => s.Reason));
        Assert.Contains("BEGIN-DIFF-", stub.Requests[0].Body);
        Assert.DoesNotContain("package-lock", stub.Requests[0].Body);
    }

    [Fact]
    public void User_message_nonce_differs_per_call() =>
        Assert.NotEqual(Review.BuildUserMessage([]), Review.BuildUserMessage([]));

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
