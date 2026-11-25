namespace SmartWizz.Tests;

public sealed class CSharpAnalysisTests
{
    static FileDiff File(string code, bool added = true)
    {
        var file = new FileDiff { Path = "src/A.cs" };
        int line = 10;
        foreach (string text in code.Split('\n'))
        {
            file.NewLines.Add(line, text);
            if (added) file.AddedLines.Add(line);
            file.Annotated.AppendLine(text);
            line++;
        }
        return file;
    }

    [Theory]
    [InlineData("for (int i = 0; i <= values.Length; i++) result += values[i];", true)]
    [InlineData("for (int i = 0; i < values.Length; i++) result += values[i];", false)]
    [InlineData("for (int i = 0; i <= values.Length; i++) result += other[i];", false)]
    [InlineData("for (int i = 0; i <= values.Length; i++) { if (i == values.Length) break; result += values[i]; }", false)]
    [InlineData("// for (int i = 0; i <= values.Length; i++) result += values[i];", false)]
    public void Recognizes_only_narrow_visible_patterns(string code, bool expected)
    {
        List<Finding> findings = CSharpAnalysis.Analyze(File(code), default);
        if (!expected) Assert.Empty(findings);
        else
        {
            Finding finding = Assert.Single(findings);
            Assert.Equal(10, finding.StartLine);
            Assert.Equal("off-by-one", finding.RuleId);
        }
    }

    [Fact]
    public void Context_only_loop_is_not_reported() =>
        Assert.Empty(CSharpAnalysis.Analyze(File("for (int i = 0; i <= values.Length; i++) result += values[i];", false), default));

    [Fact]
    public async Task Static_finding_survives_model_omission()
    {
        var stub = new StubHandler((_, _) => StubHandler.Chat("""{"findings":[]}"""));
        var provider = new Provider(new HttpClient(stub), new ProviderConfig { Endpoint = "http://x/v1/chat/completions", Model = "m" }, null);
        ReviewResult result = await Review.RunAsync([File("for (int i = 0; i <= values.Length; i++) result += values[i];")],
            new ReviewConfig(), provider, new ReviewPrompts("system", "review", "recheck", "standards"), default);
        Finding finding = Assert.Single(result.Findings);
        Assert.Equal(finding.Fingerprint, Assert.Single(result.StaticFindingFingerprints));
        Assert.Empty(result.FindingRequests);
        Assert.Equal(1, result.Requests);
    }
}
