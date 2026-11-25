namespace SmartWizz.Tests;

public class HistoricalRevalidationTests
{
    [Fact]
    public void Missing_unknown_and_duplicate_verdicts_cannot_confirm_a_candidate()
    {
        const string reply = """
            {"results":[{"id":"a","status":"supported"},{"id":"b","status":"supported"},
            {"id":"b","status":"not-supported"},{"id":"unknown","status":"supported"},
            {"id":"d","status":"resolved"},{"id":"e","status":"not-supported"}]}
            """;
        Dictionary<string, string> verdicts = HistoricalRevalidation.Parse(reply, ["a", "b", "c", "d", "e"]);
        Assert.Equal("supported", verdicts["a"]);
        Assert.Equal("inconclusive", verdicts["b"]);
        Assert.Equal("inconclusive", verdicts["c"]);
        Assert.Equal("inconclusive", verdicts["d"]);
        Assert.Equal("not-supported", verdicts["e"]);
        Assert.False(verdicts.ContainsKey("unknown"));
    }

    [Theory]
    [InlineData("```json\n", "\n```")]
    [InlineData("```\n", "\n```")]
    [InlineData("", "")]
    public void Accepts_raw_and_fenced_json(string prefix, string suffix)
    {
        const string payload = """{"results":[{"id":"a","status":"supported"}]}""";
        Assert.Equal("supported", HistoricalRevalidation.Parse(prefix + payload + suffix, ["a"])["a"]);
    }

    [Fact]
    public void Revalidation_disables_cache_even_when_config_enables_it()
    {
        Options opt = Options.Parse(["check", "--revalidate"])
            .ResolveStorage(new StorageConfig { Cache = true, Remember = true }, false, true, true);
        Assert.True(opt.Remember);
        Assert.False(opt.Cache);
        Assert.True(opt.RevalidateHistory);
    }

    [Theory]
    [InlineData("--cache")]
    [InlineData("--stateless")]
    [InlineData("--no-remember")]
    public void Revalidation_rejects_incompatible_options(string option)
    {
        Assert.Throws<SmartWizzException>(() => Options.Parse(["check", "--revalidate", option]));
    }

    [Fact]
    public void Candidate_excerpt_is_bounded()
    {
        var file = new FileDiff { Path = "Example.cs" };
        file.NewLines.Add(10, "short line");
        Assert.NotNull(HistoricalRevalidation.CreateCandidate("id", Severity.High, "off-by-one", file, 10));
        file.NewLines[10] = new string('x', 1001);
        Assert.Null(HistoricalRevalidation.CreateCandidate("id", Severity.High, "off-by-one", file, 10));
    }
}
