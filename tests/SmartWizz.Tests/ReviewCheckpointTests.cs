namespace SmartWizz.Tests;

public class ReviewCheckpointTests
{
    static readonly string First = new('a', 40), Second = new('b', 40), Third = new('c', 40);

    static Finding Issue(string fingerprint, Severity severity = Severity.High, string path = "src/A.cs") =>
        new("null-check", severity, "Missing null check", "Validate input", path, 12, null, 0.9, fingerprint);

    static ReviewCheckpoint Initial(params Finding[] findings) => ReviewCheckpoint.Advance(First, null, null, findings, []);

    [Fact]
    public void New_findings_start_open_and_block_the_gate()
    {
        TrackedFinding tracked = Assert.Single(Initial(Issue("x")).Findings);
        Assert.Equal(FindingStatus.Open, tracked.Status);
        Assert.Equal(First, tracked.FirstSeenSha);
        Assert.Equal(1, Review.ExitCode(Initial(Issue("x")).UnresolvedFindings, Severity.High));
    }

    [Fact]
    public void Absent_from_rerun_without_recheck_stays_unresolved()
    {
        var next = ReviewCheckpoint.Advance(Second, null, Initial(Issue("old")), [Issue("new")], []);

        Assert.Equal(Second, next.HeadSha);
        Assert.Equal(2, next.UnresolvedFindings.Count);
        Assert.Equal(1, Review.ExitCode(next.UnresolvedFindings, Severity.High));
    }

    [Fact]
    public void Verified_fix_resolves_and_unblocks_the_gate()
    {
        var next = ReviewCheckpoint.Advance(Second, null, Initial(Issue("old")), [],
            [new Recheck("old", true, "guard added", "src/A.cs")]);

        TrackedFinding tracked = Assert.Single(next.Findings);
        Assert.Equal(FindingStatus.Resolved, tracked.Status);
        Assert.Equal("guard added", tracked.Resolution);
        Assert.Equal(Second, tracked.LastCheckedSha);
        Assert.Equal(0, Review.ExitCode(next.UnresolvedFindings, Severity.Low));
    }

    [Fact]
    public void Recheck_that_does_not_confirm_a_fix_keeps_the_finding_open()
    {
        var next = ReviewCheckpoint.Advance(Second, null, Initial(Issue("old")), [],
            [new Recheck("old", false, "not confirmed resolved", "src/Renamed.cs")]);

        TrackedFinding tracked = Assert.Single(next.Findings);
        Assert.Equal(FindingStatus.Open, tracked.Status);
        Assert.Equal(First, tracked.FirstSeenSha);
        Assert.Equal(Second, tracked.LastCheckedSha);
        Assert.Equal("src/Renamed.cs", tracked.Finding.FilePath);
    }

    [Fact]
    public void Resolved_finding_reported_again_is_regressed()
    {
        var resolved = ReviewCheckpoint.Advance(Second, null, Initial(Issue("x")), [], [new Recheck("x", true, "fixed", "src/A.cs")]);
        var next = ReviewCheckpoint.Advance(Third, null, resolved, [Issue("x")], []);

        TrackedFinding tracked = Assert.Single(next.Findings);
        Assert.Equal(FindingStatus.Regressed, tracked.Status);
        Assert.Equal(First, tracked.FirstSeenSha);
        Assert.Equal(1, Review.ExitCode(next.UnresolvedFindings, Severity.High));
    }

    [Fact]
    public void Resolved_findings_are_kept_as_history_and_never_rechecked()
    {
        var resolved = ReviewCheckpoint.Advance(Second, null, Initial(Issue("x")), [], [new Recheck("x", true, "fixed", "src/A.cs")]);
        var next = ReviewCheckpoint.Advance(Third, null, resolved, [], [new Recheck("x", false, "ignored", "src/A.cs")]);

        TrackedFinding tracked = Assert.Single(next.Findings);
        Assert.Equal(FindingStatus.Resolved, tracked.Status);
        Assert.Equal(Second, tracked.LastCheckedSha);
    }

    [Fact]
    public void Duplicate_fingerprint_is_not_added_twice()
    {
        var next = ReviewCheckpoint.Advance(Second, null, Initial(Issue("same")), [Issue("same", Severity.Critical)], []);
        TrackedFinding tracked = Assert.Single(next.Findings);
        Assert.Equal(Severity.Critical, tracked.Finding.Severity);
        Assert.Equal(First, tracked.FirstSeenSha);
    }

    [Fact]
    public void Checkpoint_requires_a_full_commit_sha()
    {
        Assert.Throws<SmartWizzException>(() => ReviewCheckpoint.Advance("HEAD", null, null, [], []));
    }

    [Fact]
    public void Checkpoint_roundtrips_with_scope_and_status()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var state = ReviewCheckpoint.Advance(Second, new string('f', 64), Initial(Issue("done"), Issue("open")), [],
                [new Recheck("done", true, "fixed", "src/A.cs")]);
            state.Save(path);
            var log = new StringWriter();
            ReviewCheckpoint restored = ReviewCheckpoint.Load(path, log) ?? throw new InvalidOperationException("not restored");

            Assert.Equal(Second, restored.HeadSha);
            Assert.Equal(new string('f', 64), restored.ReviewScopeSha256);
            Assert.Equal(FindingStatus.Resolved, restored.Findings.Single(t => t.Finding.Fingerprint == "done").Status);
            Assert.Equal("open", Assert.Single(restored.UnresolvedFindings).Fingerprint);
            Assert.Empty(log.ToString());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("""{"version":1,"headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","openFindings":[]}""")]
    [InlineData("""{"version":2,"headSha":"bad","findings":[]}""")]
    [InlineData("""{"version":2,"headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","findings":[{"finding":null,"status":"open"}]}""")]
    [InlineData("""{"version":2,"headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","findings":[{"finding":{"ruleId":"r","severity":"high","title":"t","message":"m","filePath":"a","startLine":1,"confidence":1,"fingerprint":"x"},"status":7,"firstSeenSha":"a","lastCheckedSha":"a"}]}""")]
    [InlineData("not json")]
    public void Unusable_checkpoint_falls_back_to_a_complete_review(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, content);
            var log = new StringWriter();
            Assert.Null(ReviewCheckpoint.Load(path, log));
            Assert.Contains("running a complete review", log.ToString());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Checkpoint_with_duplicate_fingerprints_is_rejected()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Initial(Issue("x")).Save(path);
            string json = File.ReadAllText(path);
            int start = json.IndexOf('[', StringComparison.Ordinal), end = json.LastIndexOf(']');
            string entry = json[(start + 1)..end];
            File.WriteAllText(path, json[..(start + 1)] + entry + "," + entry + json[end..]);
            Assert.Null(ReviewCheckpoint.Load(path, new StringWriter()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
