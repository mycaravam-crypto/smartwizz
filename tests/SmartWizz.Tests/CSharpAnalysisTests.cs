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
        Dictionary<int, int> map = CSharpAnalysis.AttributionMap(File(code), default);
        if (!expected) Assert.Empty(map);
        else Assert.Equal(10, map[10]);
    }

    [Fact]
    public void Context_only_loop_is_not_reported() =>
        Assert.Empty(CSharpAnalysis.AttributionMap(File("for (int i = 0; i <= values.Length; i++) result += values[i];", false), default));

    [Fact]
    public void Relocates_only_supported_findings_within_visible_loop()
    {
        FileDiff file = File("for (int i = 0; i <= values.Length; i++)\n{\n    result += values[i];\n}");
        Dictionary<int, int> map = CSharpAnalysis.AttributionMap(file, default);
        var original = new Finding("off-by-one", Severity.High, "t", "m", file.Path, 12, 13, 0.9, "old", "fix");
        Finding adjusted = CSharpAnalysis.Attribute(original, file, map);
        Assert.Equal(10, adjusted.StartLine);
        Assert.Null(adjusted.EndLine);
        Assert.Null(adjusted.SuggestedFix);
        Assert.Equal(Severity.High, adjusted.Severity);
        Assert.Equal(Review.Fingerprint("off-by-one", file.Path, file.NewLines[10]), adjusted.Fingerprint);
        Finding unrelated = original with { RuleId = "null-dereference" };
        Assert.Same(unrelated, CSharpAnalysis.Attribute(unrelated, file, map));
        Finding outside = original with { StartLine = 9 };
        Assert.Same(outside, CSharpAnalysis.Attribute(outside, file, map));
        file.AddedLines.Remove(10);
        Assert.Empty(CSharpAnalysis.AttributionMap(file, default));
    }

    [Fact]
    public async Task No_finding_is_created_when_model_omits_it()
    {
        var stub = new StubHandler((_, _) => StubHandler.Chat("""{"findings":[]}"""));
        var provider = new Provider(new HttpClient(stub), new ProviderConfig { Endpoint = "http://x/v1/chat/completions", Model = "m" }, null);
        ReviewResult result = await Review.RunAsync([File("for (int i = 0; i <= values.Length; i++) result += values[i];")],
            new ReviewConfig(), provider, new ReviewPrompts("system", "review", "recheck", "standards"), default);
        Assert.Empty(result.Findings);
        Assert.Empty(result.SourceAttributionOriginalLines);
        Assert.Empty(result.FindingRequests);
        Assert.Equal(1, result.Requests);
    }
}
