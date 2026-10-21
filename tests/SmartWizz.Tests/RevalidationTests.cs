namespace SmartWizz.Tests;

public class RevalidationTests
{
    static readonly string Sha = new('a', 40);

    static TrackedFinding Tracked(string fingerprint, string path = "src/A.cs", FindingStatus status = FindingStatus.Open) =>
        new(new Finding("null-check", Severity.High, "Missing null check", "Validate input", path, 3, null, 0.9, fingerprint),
            status, Sha, Sha);

    static List<Revalidation.Candidate> Candidates(params string[] fingerprints) =>
        fingerprints.Select(f => new Revalidation.Candidate(Tracked(f), "src/A.cs")).ToList();

    static FileDiff Changed(string path, string? oldPath = null) => new() { Path = path, OldPath = oldPath };

    [Fact]
    public void Explicit_resolution_with_reason_resolves()
    {
        Recheck check = Assert.Single(Revalidation.Parse(
            """{"results":[{"fingerprint":"x","status":"resolved","reason":"null guard added on line 4"}]}""", Candidates("x")));
        Assert.True(check.Resolved);
        Assert.Equal("null guard added on line 4", check.Note);
    }

    [Theory]
    [InlineData("""{"results":[]}""")]
    [InlineData("""{"results":[{"fingerprint":"x","status":"resolved"}]}""")]
    [InlineData("""{"results":[{"fingerprint":"x","status":"open","reason":"still dereferenced"}]}""")]
    [InlineData("""{"results":[{"fingerprint":"x","status":"fixed","reason":"looks fine"}]}""")]
    [InlineData("""{"results":[{"fingerprint":"x","status":"resolved","reason":"a"},{"fingerprint":"x","status":"open","reason":"b"}]}""")]
    [InlineData("""{"results":[{"fingerprint":"other","status":"resolved","reason":"unrelated"}, 42]}""")]
    public void Anything_but_an_unambiguous_resolution_keeps_the_finding_open(string reply)
    {
        Recheck check = Assert.Single(Revalidation.Parse(reply, Candidates("x")));
        Assert.False(check.Resolved);
        Assert.Equal("x", check.Fingerprint);
    }

    [Theory]
    [InlineData("no json here")]
    [InlineData("""{"results": "nope"}""")]
    [InlineData("""{"results": [ }""")]
    public void Malformed_revalidation_output_fails_the_run(string reply)
    {
        SmartWizzException e = Assert.Throws<SmartWizzException>(() => Revalidation.Parse(reply, Candidates("x")));
        Assert.Equal(FailureKind.InvalidModelOutput, e.Kind);
    }

    [Fact]
    public void Resolution_reason_is_sanitized()
    {
        Recheck check = Assert.Single(Revalidation.Parse(
            """{"results":[{"fingerprint":"x","status":"resolved","reason":"fixed\u001b[31m now"}]}""", Candidates("x")));
        Assert.Equal("fixed[31m now", check.Note);
    }

    [Fact]
    public void Excerpt_follows_the_anchored_line_after_it_moved()
    {
        string anchor = "    int x = input.Value;";
        Finding finding = Tracked(Review.Fingerprint("null-check", "src/A.cs", anchor)).Finding;
        string content = string.Concat(Enumerable.Range(1, 200).Select(i => $"line {i}\n")) + anchor + "\n";

        string excerpt = Revalidation.Excerpt(finding, content);

        Assert.Contains($"   201 |{anchor}", excerpt);
        Assert.DoesNotContain("     3 |", excerpt);
    }

    [Fact]
    public void Excerpt_truncates_very_long_lines()
    {
        string excerpt = Revalidation.Excerpt(Tracked("unknown").Finding, new string('x', 5000));
        Assert.True(excerpt.Length < 1100);
        Assert.EndsWith("...\n", excerpt);
    }

    [Fact]
    public void Excerpt_falls_back_to_the_reported_line()
    {
        string excerpt = Revalidation.Excerpt(Tracked("unknown").Finding, "a\nb\nc\nd\n");
        Assert.StartsWith("     1 |a\n", excerpt);
    }

    [Fact]
    public void Incremental_review_only_rechecks_findings_in_changed_files()
    {
        var previous = new ReviewCheckpoint
        {
            HeadSha = Sha,
            Findings = [Tracked("touched"), Tracked("untouched", "src/B.cs"), Tracked("renamed", "src/Old.cs"),
                Tracked("reported"), Tracked("done", status: FindingStatus.Resolved)],
        };
        Finding reported = Tracked("reported").Finding;
        List<FileDiff> files = [Changed("src/A.cs"), Changed("src/New.cs", "src/Old.cs")];

        List<Revalidation.Candidate> incremental = Revalidation.SelectCandidates(previous, [reported], files, ReviewMode.Incremental);
        List<Revalidation.Candidate> full = Revalidation.SelectCandidates(previous, [reported], files, ReviewMode.Full);

        Assert.Equal(["touched", "renamed"], incremental.Select(c => c.Tracked.Finding.Fingerprint));
        Assert.Equal("src/New.cs", incremental[1].CurrentPath);
        Assert.Equal(["touched", "untouched", "renamed"], full.Select(c => c.Tracked.Finding.Fingerprint));
        Assert.Empty(Revalidation.SelectCandidates(null, [], files, ReviewMode.Full));
    }
}
