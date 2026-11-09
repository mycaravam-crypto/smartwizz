namespace SmartWizz;

public sealed record ReviewPrompts(string Review, string Revalidation);

public sealed record ReviewRun(ReviewPlan Plan, ReviewResult Result, ReviewCheckpoint? Checkpoint);

// Plan, review, re-check earlier findings and compute the next checkpoint. Persists nothing.
public static class IncrementalReview
{
    public static Task<string> LocalDiffAsync(Options opt, int contextLines, CancellationToken ct) => opt.Staged
        ? GitDiff.RunStagedAsync(opt.Repo, contextLines, ct)
        : GitDiff.RunUncommittedAsync(opt.Repo, contextLines, ct);

    public static async Task<ReviewRun> RunAsync(Options opt, ReviewCheckpoint? previous, ReviewConfig cfg, Provider provider,
        ReviewPrompts prompts, string reviewScope, RuleCatalog? catalog, TextWriter log, CancellationToken ct)
    {
        if (opt.Staged || opt.Check)
        {
            ReviewPlan localPlan = opt.Staged ? new(ReviewMode.Staged, null, "git index") : new(ReviewMode.Uncommitted, null, "working tree vs HEAD");
            log.WriteLine($"review mode: {localPlan.Mode.ToString().ToLowerInvariant()} ({localPlan.Reason})");
            if (!opt.Staged && await GitDiff.ListUntrackedAsync(opt.Repo, ct) is { Count: > 0 } untracked)
                log.WriteLine($"note: untracked, not reviewed (`git add -N <path>` includes them): {string.Join(", ", untracked)}");
            List<FileDiff> localFiles = GitDiff.Parse(await LocalDiffAsync(opt, cfg.ContextLines, ct));
            ReviewResult localResult = await Review.RunAsync(localFiles, cfg, provider, prompts.Review, ct, catalog);
            return new ReviewRun(localPlan, localResult, null);
        }

        ReviewPlan plan = await ReviewPlan.DecideAsync(opt.Repo, opt.Head, opt.Since, opt.StateIn is not null, previous, opt.FullReview, ct);
        log.WriteLine($"review mode: {plan.Mode.ToString().ToLowerInvariant()} ({plan.Reason})");
        List<FileDiff> files = GitDiff.Parse(await GitDiff.RunIncrementalAsync(opt.Repo, opt.Base!, opt.Head, plan.SinceSha, cfg.ContextLines, ct));
        ReviewResult result = await Review.RunAsync(files, cfg, provider, prompts.Review, ct, catalog);
        if (opt.StateOut is null)
            return new ReviewRun(plan, result, null);

        string headSha = await GitDiff.ResolveShaAsync(opt.Repo, opt.Head, ct);
        List<Revalidation.Candidate> candidates = Revalidation.SelectCandidates(previous, result.Findings, files, plan.Mode);
        List<Recheck> rechecks = await Revalidation.RecheckAsync(opt.Repo, headSha, candidates, cfg, provider, prompts.Revalidation, ct);
        return new ReviewRun(plan, result, ReviewCheckpoint.Advance(headSha, reviewScope, previous, result.Findings, rechecks));
    }
}
