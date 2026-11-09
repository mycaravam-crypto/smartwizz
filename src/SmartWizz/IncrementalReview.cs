namespace SmartWizz;

// System is shared by every review request. Review and Standards are task texts placed after the diff:
// Review for the general review (pass 1), Standards for follow-up passes that check only ruleset checks.
public sealed record ReviewPrompts(string System, string Review, string Revalidation, string Standards);

public sealed record ReviewRun(ReviewPlan Plan, ReviewResult Result, ReviewCheckpoint? Checkpoint);

// Plan, review, re-check earlier findings and compute the next checkpoint. Persists nothing.
public static class IncrementalReview
{
    public static Task<string> LocalDiffAsync(Options opt, int contextLines, CancellationToken ct) => opt.Staged
        ? GitDiff.RunStagedAsync(opt.Repo, contextLines, ct)
        : GitDiff.RunUncommittedAsync(opt.Repo, contextLines, ct);

    public static async Task<ReviewRun> RunAsync(Options opt, ReviewCheckpoint? previous, ReviewConfig cfg, Provider provider,
        ReviewPrompts prompts, string reviewScope, Ruleset? ruleset, TextWriter log, CancellationToken ct)
    {
        if (opt.Staged || opt.Check)
        {
            ReviewPlan localPlan = opt.Staged ? new(ReviewMode.Staged, null, "git index") : new(ReviewMode.Uncommitted, null, "working tree vs HEAD");
            log.WriteLine($"review mode: {localPlan.Mode.ToString().ToLowerInvariant()} ({localPlan.Reason})");
            if (!opt.Staged && await GitDiff.ListUntrackedAsync(opt.Repo, ct) is { Count: > 0 } untracked)
                log.WriteLine($"note: untracked, not reviewed (`git add -N <path>` includes them): {string.Join(", ", untracked)}");
            List<FileDiff> localFiles = GitDiff.Parse(await LocalDiffAsync(opt, cfg.ContextLines, ct));
            ReviewResult localResult = await Review.RunAsync(localFiles, cfg, provider, prompts, ct, ruleset);
            return new ReviewRun(localPlan, localResult, null);
        }

        ReviewPlan plan = await ReviewPlan.DecideAsync(opt.Repo, opt.Head, opt.Since, opt.StateIn is not null, previous, reviewScope, opt.FullReview, ct);
        log.WriteLine($"review mode: {plan.Mode.ToString().ToLowerInvariant()} ({plan.Reason})");
        List<FileDiff> files = GitDiff.Parse(await GitDiff.RunIncrementalAsync(opt.Repo, opt.Base!, opt.Head, plan.SinceSha, cfg.ContextLines, ct));
        List<FileDiff> toReview = plan.Mode == ReviewMode.Incremental
            ? OwnChanges(files, GitDiff.Parse(await GitDiff.RunAsync(opt.Repo, opt.Base!, opt.Head, cfg.ContextLines, ct)))
            : files;
        ReviewResult result = await Review.RunAsync(toReview, cfg, provider, prompts, ct, ruleset);
        if (opt.StateOut is null)
            return new ReviewRun(plan, result, null);

        string headSha = await GitDiff.ResolveShaAsync(opt.Repo, opt.Head, ct);
        List<Revalidation.Candidate> candidates = Revalidation.SelectCandidates(previous, result.Findings, files, plan.Mode);
        List<Recheck> rechecks = await Revalidation.RecheckAsync(opt.Repo, headSha, candidates, cfg, provider, prompts.Revalidation, ct);
        return new ReviewRun(plan, result, ReviewCheckpoint.Advance(headSha, reviewScope, previous, result.Findings, rechecks));
    }

    // since..head also holds whatever merging the base brought in: only lines in the PR's own diff can be reported.
    // Re-checks keep the whole delta, because a fix can vanish from the PR diff (e.g. deleting a file the PR added).
    static List<FileDiff> OwnChanges(List<FileDiff> delta, List<FileDiff> prFiles)
    {
        var pr = prFiles.ToDictionary(f => f.Path);
        List<FileDiff> own = [];
        foreach (FileDiff f in delta)
        {
            if (!pr.TryGetValue(f.Path, out FileDiff? prFile)) continue;
            bool hadAddedLines = f.AddedLines.Count > 0;
            f.AddedLines.IntersectWith(prFile.AddedLines);
            if (!hadAddedLines || f.AddedLines.Count > 0) own.Add(f);
        }
        return own;
    }
}
