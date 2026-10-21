namespace SmartWizz;

public sealed record ReviewPrompts(string Review, string Revalidation);

public sealed record ReviewRun(ReviewPlan Plan, ReviewResult Result, ReviewCheckpoint? Checkpoint);

// Plan, review, re-check earlier findings and compute the next checkpoint. Persists nothing.
public static class IncrementalReview
{
    public static async Task<ReviewRun> RunAsync(Options opt, ReviewCheckpoint? previous, ReviewConfig cfg, Provider provider,
        ReviewPrompts prompts, string reviewScope, RuleCatalog? catalog, TextWriter log, CancellationToken ct)
    {
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
