namespace SmartWizz.Tests;

public class ReviewCheckpointTests
{
    static Finding Issue(string fingerprint, Severity severity = Severity.High) =>
        new("null-check", severity, "Missing null check", "Validate input", "src/A.cs", 12, null, 0.9, fingerprint);

    [Fact]
    public void Incremental_merge_keeps_unresolved_findings_and_new_issues()
    {
        string firstSha = new('a', 40), nextSha = new('b', 40);
        var original = ReviewCheckpoint.Merge(firstSha, null, [Issue("first")]);
        var next = ReviewCheckpoint.Merge(nextSha, original, [Issue("second")]);

        Assert.Equal(nextSha, next.HeadSha);
        Assert.Equal(2, next.OpenFindings.Count);
        Assert.Equal(1, Review.ExitCode(next.OpenFindings, Severity.High));
    }

    [Fact]
    public void Duplicate_fingerprint_is_not_added_twice()
    {
        var previous = ReviewCheckpoint.Merge(new string('a', 40), null, [Issue("same")]);
        var next = ReviewCheckpoint.Merge(new string('b', 40), previous, [Issue("same", Severity.Critical)]);
        Finding finding = Assert.Single(next.OpenFindings);
        Assert.Equal(Severity.Critical, finding.Severity);
    }

    [Fact]
    public void Checkpoint_records_review_scope()
    {
        ReviewCheckpoint state = ReviewCheckpoint.Merge(new string('a', 40), null, [Issue("x")], new string('b', 64));
        Assert.Equal(new string('b', 64), state.ReviewScopeSha256);
    }

    [Fact]
    public void Checkpoint_roundtrips_and_rejects_invalid_version()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ReviewCheckpoint state = ReviewCheckpoint.Merge(new string('a', 40), null, [Issue("persistent")]);
            state.Save(path);
            var restored = ReviewCheckpoint.Load(path)!;
            Assert.Equal(state.HeadSha, restored.HeadSha);
            Assert.Equal("persistent", Assert.Single(restored.OpenFindings).Fingerprint);
            File.WriteAllText(path, """{"version":99,"headSha":"bad","openFindings":[]}""");
            Assert.Throws<SmartWizzException>(() => ReviewCheckpoint.Load(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
