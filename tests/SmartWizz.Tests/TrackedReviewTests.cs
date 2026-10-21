using System.Net;

namespace SmartWizz.Tests;

// End-to-end lifecycle over a real git history with a scripted model: plan, review, recheck, next checkpoint.
[Collection("process-env")] // runs real git: must not overlap GitCancellationTests' fake git on PATH
public sealed class TrackedReviewTests : IDisposable
{
    const string Defect = "    int X(Input input) => input.Value;";
    const string NoFindings = """{"findings":[]}""";
    static readonly ReviewPrompts Prompts = new("REVIEW-PROMPT", "RECHECK-PROMPT");

    readonly string _dir = Path.Combine(Path.GetTempPath(), "smartwizz-" + Guid.NewGuid().ToString("N"));
    readonly Queue<Func<HttpResponseMessage>> _reviews = new();
    readonly Queue<Func<HttpResponseMessage>> _rechecks = new();
    readonly List<string> _reviewRequests = [];
    readonly List<string> _recheckRequests = [];
    readonly Provider _provider;

    public TrackedReviewTests()
    {
        Directory.CreateDirectory(_dir);
        IncrementalGitTests.Git(_dir, "init", "-b", "main");
        IncrementalGitTests.Git(_dir, "config", "user.email", "test@example.invalid");
        IncrementalGitTests.Git(_dir, "config", "user.name", "SmartWizz Test");
        Commit("base", ("README.md", "base\n"));
        IncrementalGitTests.Git(_dir, "checkout", "-b", "feature");

        var model = new StubHandler((_, body) =>
        {
            if (body is not null && body.Contains("RECHECK-PROMPT", StringComparison.Ordinal))
            {
                _recheckRequests.Add(body);
                return _rechecks.Dequeue()();
            }
            _reviewRequests.Add(body ?? "");
            return _reviews.Dequeue()();
        });
        _provider = new Provider(new HttpClient(model), new ProviderConfig { Endpoint = "http://llm/v1", Model = "m", MaxRetries = 0 },
            null, delay: StubHandler.NoDelay);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Commit(string message, params (string Path, string? Content)[] files)
    {
        foreach ((string path, string? content) in files)
        {
            string full = Path.Combine(_dir, path);
            if (content is null) File.Delete(full);
            else File.WriteAllText(full, content);
        }
        IncrementalGitTests.Git(_dir, "add", "-A");
        IncrementalGitTests.Git(_dir, "commit", "-m", message);
        return IncrementalGitTests.Sha(_dir);
    }

    static string ClassWith(string member, string name = "A") => $"class {name}\n{{\n{member}\n}}\n";

    static Func<HttpResponseMessage> Finding(string path, int line) => () => StubHandler.Chat(
        $$"""{"findings":[{"ruleId":"null-dereference","severity":"high","title":"Possible null","message":"Guard it","filePath":"{{path}}","startLine":{{line}},"confidence":0.9}]}""");

    static Func<HttpResponseMessage> Reply(string content) => () => StubHandler.Chat(content);

    static Func<HttpResponseMessage> Verdict(string fingerprint, string status) => () => StubHandler.Chat(
        $$"""{"results":[{"fingerprint":"{{fingerprint}}","status":"{{status}}","reason":"checked the current code"}]}""");

    static readonly string DefectFingerprint = Review.Fingerprint("null-dereference", "a.cs", Defect);

    Task<ReviewRun> Run(string head, ReviewCheckpoint? previous, bool fullReview = false) =>
        IncrementalReview.RunAsync(
            new Options
            {
                Base = "main",
                Head = head,
                Repo = _dir,
                Since = previous?.HeadSha,
                FullReview = fullReview,
                StateIn = previous is null ? null : "previous.json",
                StateOut = "next.json",
            },
            previous, new ReviewConfig(), _provider, Prompts, "scope", null, TextWriter.Null, default);

    async Task<ReviewCheckpoint> InitialReview()
    {
        string head = Commit("initial PR", ("a.cs", ClassWith(Defect)));
        _reviews.Enqueue(Finding("a.cs", 3));
        ReviewRun run = await Run(head, null);
        Assert.Equal(ReviewMode.Full, run.Plan.Mode);
        ReviewCheckpoint checkpoint = run.Checkpoint ?? throw new InvalidOperationException("no checkpoint");
        Assert.Equal(DefectFingerprint, Assert.Single(checkpoint.UnresolvedFindings).Fingerprint);
        return checkpoint;
    }

    [Fact]
    public async Task Normal_fix_is_resolved_after_verification_and_new_defect_in_fix_blocks()
    {
        ReviewCheckpoint first = await InitialReview();
        string head = Commit("fix and new code",
            ("a.cs", ClassWith("    int X(Input? input) => input?.Value ?? 0;")),
            ("b.cs", ClassWith("    string S(string s) => s.Substring(5);", "B")));
        _reviews.Enqueue(Finding("b.cs", 3));
        _rechecks.Enqueue(Verdict(DefectFingerprint, "resolved"));

        ReviewRun run = await Run(head, first);

        Assert.Equal(ReviewMode.Incremental, run.Plan.Mode);
        Assert.Contains("input?.Value ?? 0", Assert.Single(_recheckRequests));
        ReviewCheckpoint next = run.Checkpoint ?? throw new InvalidOperationException("no checkpoint");
        Assert.Equal(FindingStatus.Resolved, next.Findings.Single(t => t.Finding.FilePath == "a.cs").Status);
        Finding open = Assert.Single(next.UnresolvedFindings);
        Assert.Equal("b.cs", open.FilePath);
        Assert.Equal(1, Review.ExitCode(next.UnresolvedFindings, Severity.High));
    }

    [Fact]
    public async Task Unresolved_finding_keeps_blocking_and_untouched_files_are_not_rechecked()
    {
        ReviewCheckpoint first = await InitialReview();
        string unrelated = Commit("unrelated", ("c.cs", ClassWith("    int Y() => 1;", "C")));
        _reviews.Enqueue(Reply(NoFindings));

        ReviewRun second = await Run(unrelated, first);

        Assert.Empty(_recheckRequests);
        ReviewCheckpoint afterUnrelated = second.Checkpoint ?? throw new InvalidOperationException("no checkpoint");
        Assert.Equal(1, Review.ExitCode(afterUnrelated.UnresolvedFindings, Severity.High));

        string touched = Commit("touch a.cs without fixing", ("a.cs", "// comment\n" + ClassWith(Defect)));
        _reviews.Enqueue(Reply(NoFindings));
        _rechecks.Enqueue(Verdict(DefectFingerprint, "open"));

        ReviewRun third = await Run(touched, afterUnrelated);

        Assert.Contains("     4 |    int X(Input input)", Assert.Single(_recheckRequests)); // JSON escapes "=>"
        TrackedFinding tracked = Assert.Single(third.Checkpoint?.Findings ?? []);
        Assert.Equal(FindingStatus.Open, tracked.Status);
        Assert.Equal(first.HeadSha, tracked.FirstSeenSha);
        Assert.Equal(touched, tracked.LastCheckedSha);
    }

    [Fact]
    public async Task Rebase_falls_back_to_a_complete_review_without_losing_findings()
    {
        ReviewCheckpoint first = await InitialReview();
        IncrementalGitTests.Git(_dir, "checkout", "-B", "feature", "main");
        string rewritten = Commit("rewritten PR", ("a.cs", ClassWith(Defect)), ("d.cs", ClassWith("    int Z() => 2;", "D")));
        _reviews.Enqueue(Reply(NoFindings)); // a nondeterministic model misses the old defect this time
        _rechecks.Enqueue(Verdict(DefectFingerprint, "open"));

        ReviewRun run = await Run(rewritten, first);

        Assert.Equal(ReviewMode.Full, run.Plan.Mode);
        Assert.Equal("history rewritten", run.Plan.Reason);
        Assert.Single(_recheckRequests);
        Assert.Equal(DefectFingerprint, Assert.Single(run.Checkpoint?.UnresolvedFindings ?? []).Fingerprint);
    }

    [Fact]
    public async Task Unknown_previous_commit_falls_back_to_a_complete_review()
    {
        ReviewCheckpoint first = await InitialReview();
        var forgotten = ReviewCheckpoint.Advance(new string('f', 40), "scope", null, first.UnresolvedFindings, []);
        string head = Commit("after force-push", ("g.cs", ClassWith("    int G() => 5;", "G")));
        _reviews.Enqueue(Reply(NoFindings));
        _rechecks.Enqueue(Verdict(DefectFingerprint, "open"));

        ReviewRun run = await Run(head, forgotten);

        Assert.Equal(ReviewMode.Full, run.Plan.Mode);
        Assert.Equal("previously reviewed commit not found", run.Plan.Reason);
        Assert.Single(run.Checkpoint?.UnresolvedFindings ?? []);
    }

    [Fact]
    public async Task Deleted_file_resolves_without_asking_the_model()
    {
        ReviewCheckpoint first = await InitialReview();
        string head = Commit("remove a.cs", ("a.cs", null), ("e.cs", ClassWith("    int E() => 3;", "E")));
        _reviews.Enqueue(Reply(NoFindings));

        ReviewRun run = await Run(head, first);

        Assert.Empty(_recheckRequests);
        TrackedFinding tracked = Assert.Single(run.Checkpoint?.Findings ?? []);
        Assert.Equal(FindingStatus.Resolved, tracked.Status);
        Assert.Equal("file no longer exists", tracked.Resolution);
    }

    [Fact]
    public async Task Full_review_override_reviews_the_complete_diff()
    {
        ReviewCheckpoint first = await InitialReview();
        string head = Commit("small follow-up", ("f.cs", ClassWith("    int F() => 4;", "F")));
        _reviews.Enqueue(Reply(NoFindings));
        _rechecks.Enqueue(Verdict(DefectFingerprint, "open"));

        ReviewRun run = await Run(head, first, fullReview: true);

        Assert.Equal(ReviewMode.Full, run.Plan.Mode);
        Assert.Equal("full review requested", run.Plan.Reason);
        Assert.Contains("File: a.cs", _reviewRequests[^1]);
        Assert.Single(_recheckRequests);
        Assert.Single(run.Checkpoint?.UnresolvedFindings ?? []);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_run_produces_no_checkpoint(bool reviewFails)
    {
        ReviewCheckpoint first = await InitialReview();
        string head = Commit("touch a.cs", ("a.cs", "// comment\n" + ClassWith(Defect)));
        if (reviewFails)
        {
            _reviews.Enqueue(() => StubHandler.Json("""{"error":"bad request"}""", HttpStatusCode.BadRequest));
        }
        else
        {
            _reviews.Enqueue(Reply(NoFindings));
            _rechecks.Enqueue(Reply("the finding looks fixed to me"));
        }

        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() => Run(head, first));

        Assert.Equal(reviewFails ? FailureKind.Provider : FailureKind.InvalidModelOutput, e.Kind);
    }
}
