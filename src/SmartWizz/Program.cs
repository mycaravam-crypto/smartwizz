using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartWizz;

public static class Program
{
    const string Usage = """
        Usage: smartwizz check [--staged] [--repo <path>] [--config <path>] [--format <name>] [--json <path>] [--annotated-diff <path>] [--fail-on <sev>]
               smartwizz (--base <ref> | --pr <id> | --staged) [options]

          check               Review your uncommitted changes (with --staged: only the index).
                              Config and .env come from the user config directory, never the reviewed repo.
          --base <ref>        Target branch/commit the PR merges into, e.g. origin/main
          --pr <id>           Resolve Azure DevOps PR via ado-devops CLI (read-only)
          --staged            Review only staged (index) changes; no commit or PR needed
          --head <ref>        PR head (default: HEAD)
          --since <sha>       Review changes since a previous successful review SHA
          --full-review       Review the complete PR diff even when --since is given
          --repo <path>       Git checkout (default: current directory)
          --config <path>     Config file (default: ./smartwizz.json if present)
          --json <path>       Also write the JSON report to this file
          --format <name>     Console output: console (default) or problems (one line per finding, for editors)
          --annotated-diff <path>  Write review findings inline with the unified PR diff
          --state-in <path>   Load previous review checkpoint (requires --since and --state-out)
          --state-out <path>  Write review checkpoint after a completed review
          --fail-on <sev>     Exit 1 if any finding is at/above low|medium|high|critical
          --publish           Post findings as Azure DevOps PR threads
          --publish-github    Post findings as inline comments of one GitHub PR review
          -h, --help          Show this help

        Environment: SMARTWIZZ_API_KEY (provider), SMARTWIZZ_AZDO_TOKEN or SYSTEM_ACCESSTOKEN (Azure DevOps),
                     GITHUB_TOKEN or SMARTWIZZ_GITHUB_TOKEN (GitHub).
        Exit codes: 0 ok, 1 findings at/above --fail-on, 2 execution failure, 130 cancelled.
        """;

    public static async Task<int> Main(string[] args)
    {
        Options opt;
        try
        {
            opt = Options.Parse(args);
        }
        catch (SmartWizzException e)
        {
            Console.Error.WriteLine($"error [usage]: {e.Message}\n\n{Usage}");
            return 2;
        }
        if (opt.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        using CancellationTokenSource cts = new();
        // First Ctrl+C / SIGTERM cancels gracefully; a second one terminates immediately.
        void Cancel(PosixSignalContext context)
        {
            context.Cancel = !cts.IsCancellationRequested;
            cts.Cancel();
        }
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Cancel);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Cancel);

        try
        {
            return await RunAsync(opt, cts.Token);
        }
        catch (Exception e)
        {
            (int code, string message) = Failure(e, cts.IsCancellationRequested);
            Console.Error.WriteLine(message);
            return code;
        }
    }

    public const int ExitCancelled = 130; // 128 + SIGINT, the shell convention

    // Messages never include credentials: tokens live only in request headers.
    public static (int Code, string Message) Failure(Exception e, bool cancelled) => (e, cancelled) switch
    {
        (_, true) => (ExitCancelled, "cancelled: review incomplete, no result reported"),
        (SmartWizzException s, _) => (2, $"error [{Label(s.Kind)}]: {s.Message}"),
        _ => (2, $"error [unexpected]: {e.Message}"),
    };

    static string Label(FailureKind kind) => kind switch
    {
        FailureKind.Usage => "usage",
        FailureKind.Configuration => "configuration",
        FailureKind.Git => "git",
        FailureKind.Provider => "provider",
        FailureKind.Timeout => "timeout",
        FailureKind.InvalidModelOutput => "invalid-model-output",
        FailureKind.Publish => "publish",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    static async Task<int> RunAsync(Options opt, CancellationToken ct)
    {
        if (opt.Check)
        {
            // check runs inside arbitrary repos, whose ./smartwizz.json could redirect the provider and capture the API key.
            string userConfig = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "smartwizz");
            Config.LoadDotEnv(Path.Combine(userConfig, ".env"));
            opt = opt with { ConfigPath = opt.ConfigPath ?? Path.Combine(userConfig, "smartwizz.json") };
        }
        else Config.LoadDotEnv(".env");
        var cfg = Config.Load(opt.ConfigPath, Environment.GetEnvironmentVariable);
        if (opt.PrId is { } prId)
        {
            AdoPrResolver.Resolved resolved = await AdoPrResolver.ResolveAsync(opt.Repo, prId, ct);
            opt = opt with { Base = resolved.Base, Head = resolved.Head };
            if (cfg.AzureDevOps.RepositoryId is { } existing &&
                !string.Equals(existing, resolved.RepositoryId, StringComparison.OrdinalIgnoreCase))
                throw new SmartWizzException(FailureKind.Configuration, "configured Azure DevOps repository differs from the selected PR");
            if (cfg.AzureDevOps.PullRequestId is { } previousPr && previousPr != prId)
                throw new SmartWizzException(FailureKind.Configuration, "configured Azure DevOps PR ID differs from --pr");
            cfg.AzureDevOps.PullRequestId = prId;
            cfg.AzureDevOps.RepositoryId = resolved.RepositoryId;
            Console.Error.WriteLine($"Azure DevOps PR #{prId}: {resolved.TargetBranch}, head {resolved.Head[..12]}");
        }
        if (Uri.TryCreate(cfg.Provider.Endpoint, UriKind.Absolute, out Uri? endpoint) && endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
            Console.Error.WriteLine("warning: provider endpoint uses plain HTTP; code and API key are sent unencrypted");
        string? guidelines = cfg.Review.Guidelines;
        RuleCatalog? catalog = guidelines is not null && Path.GetExtension(guidelines).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? RuleCatalog.Load(guidelines, cfg.Review.LlmChecks) : null;
        string? legacyStandard = guidelines is not null && catalog is null ? await File.ReadAllTextAsync(guidelines, ct) : null;
        string systemPrompt = Review.SystemPrompt(LoadPrompt("code-review.md"), legacyStandard);
        string revalidationPrompt = LoadPrompt("revalidate.md");
        string scopeMaterial = JsonSerializer.Serialize(cfg) + systemPrompt + revalidationPrompt + (catalog?.Sha256 ?? "");
        string reviewScope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scopeMaterial)));
        ReviewCheckpoint? previous = UsableCheckpoint(ReviewCheckpoint.Load(opt.StateIn, Console.Error), opt.Since);
        // No client-wide timeout: Provider and AzureDevOpsPublisher bound every request with their configured timeout.
        using HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
        Provider provider = new(http, cfg.Provider, Environment.GetEnvironmentVariable("SMARTWIZZ_API_KEY"), log: Console.Error);
        (ReviewPlan plan, ReviewResult result, ReviewCheckpoint? next) = await IncrementalReview.RunAsync(
            opt, previous, cfg.Review, provider, new ReviewPrompts(systemPrompt, revalidationPrompt), reviewScope, catalog, Console.Error, ct);

        if (opt.Problems) Report.WriteProblems(result, Console.Out);
        else Report.WriteConsole(result, Console.Out);
        if (next is not null)
            Report.WriteCheckpoint(next, previous, Console.Out);
        if (opt.JsonPath is not null)
            await File.WriteAllTextAsync(opt.JsonPath, Report.ToJson(result, plan, next), ct);
        if (opt.AnnotatedDiffPath is not null)
        {
            string diff = opt.Staged || opt.Check
                ? await IncrementalReview.LocalDiffAsync(opt, cfg.Review.ContextLines, ct)
                : await GitDiff.RunAsync(opt.Repo, opt.Base!, opt.Head, cfg.Review.ContextLines, ct);
            await File.WriteAllTextAsync(opt.AnnotatedDiffPath, AnnotatedDiff.Render(diff, result.Findings), ct);
        }

        if (opt.Publish)
        {
            string? token = Environment.GetEnvironmentVariable("SMARTWIZZ_AZDO_TOKEN") ?? Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");
            (int posted, int present) = await new AzureDevOpsPublisher(http, cfg.AzureDevOps, token).PublishAsync(result.Findings, ct);
            Console.WriteLine($"\nPublished {posted} new thread(s); {present} already present.");
        }
        if (opt.PublishGitHub)
        {
            string? token = Environment.GetEnvironmentVariable("SMARTWIZZ_GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            (int posted, int present) = await new GitHubPublisher(http, cfg.GitHub, token).PublishAsync(result.Findings, ct);
            Console.WriteLine($"\nPosted {posted} inline comment(s) as a GitHub review; {present} already present.");
        }

        // Last step: a failed review, recheck or publish never advances the baseline.
        if (next is not null && opt.StateOut is not null) next.Save(opt.StateOut);
        return Review.ExitCode(next?.UnresolvedFindings ?? result.Findings, opt.FailOn);
    }

    // A checkpoint for another baseline is untrusted: fall back to a complete review.
    static ReviewCheckpoint? UsableCheckpoint(ReviewCheckpoint? state, string? since)
    {
        if (state is null || string.Equals(state.HeadSha, since, StringComparison.OrdinalIgnoreCase)) return state;
        Console.Error.WriteLine("warning: checkpoint head SHA does not match --since; running a complete review");
        return null;
    }

    static string LoadPrompt(string name)
    {
        using Stream stream = typeof(Program).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded prompt {name} is missing from the build");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}

public sealed record Options
{
    public string? Base { get; init; }
    public int? PrId { get; init; }
    public bool Staged { get; init; }
    public bool Check { get; init; }
    public string Head { get; init; } = "HEAD";
    public string? Since { get; init; }
    public bool FullReview { get; init; }
    public string Repo { get; init; } = ".";
    public string? ConfigPath { get; init; }
    public string? JsonPath { get; init; }
    public bool Problems { get; init; }
    public string? AnnotatedDiffPath { get; init; }
    public string? StateIn { get; init; }
    public string? StateOut { get; init; }
    public Severity? FailOn { get; init; }
    public bool Publish { get; init; }
    public bool PublishGitHub { get; init; }
    public bool Help { get; init; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new SmartWizzException(FailureKind.Usage, $"{args[i - 1]} needs a value");
            o = args[i] switch
            {
                "check" when i == 0 => o with { Check = true },
                "--base" => o with { Base = Value() },
                "--pr" => o with { PrId = int.TryParse(Value(), out int id) && id > 0 ? id : throw new SmartWizzException(FailureKind.Usage, "--pr requires a positive integer") },
                "--staged" => o with { Staged = true },
                "--head" => o with { Head = Value() },
                "--since" => o with { Since = Value() },
                "--full-review" => o with { FullReview = true },
                "--repo" => o with { Repo = Value() },
                "--config" => o with { ConfigPath = Value() },
                "--json" => o with { JsonPath = Value() },
                "--format" => Value() switch
                {
                    "console" => o with { Problems = false },
                    "problems" => o with { Problems = true },
                    _ => throw new SmartWizzException(FailureKind.Usage, $"invalid format: {args[i]}"),
                },
                "--annotated-diff" => o with { AnnotatedDiffPath = Value() },
                "--state-in" => o with { StateIn = Value() },
                "--state-out" => o with { StateOut = Value() },
                "--fail-on" => o with { FailOn = Review.ParseSeverity(Value()) ?? throw new SmartWizzException(FailureKind.Usage, $"invalid severity: {args[i]}") },
                "--publish" => o with { Publish = true },
                "--publish-github" => o with { PublishGitHub = true },
                "-h" or "--help" => o with { Help = true },
                _ => throw new SmartWizzException(FailureKind.Usage, $"unknown argument: {args[i]}"),
            };
        }
        if (o.StateIn is not null && (o.Since is null || o.StateOut is null))
            throw new SmartWizzException(FailureKind.Usage, "--state-in requires --since and --state-out");
        if (o.Since is not null && !ReviewCheckpoint.IsCommitSha(o.Since))
            throw new SmartWizzException(FailureKind.Usage, "--since must be a full 40-character commit SHA");
        if (!o.Help && (o.Staged || o.Check))
        {
            if (o.Base is not null || o.PrId is not null || o.Head != "HEAD" || o.Since is not null ||
                o.StateIn is not null || o.StateOut is not null || o.FullReview || o.Publish || o.PublishGitHub)
                throw new SmartWizzException(FailureKind.Usage,
                    "check and --staged cannot be combined with PR, commit, checkpoint, or publish options");
        }
        else if (!o.Help && ((o.PrId is null && string.IsNullOrWhiteSpace(o.Base)) || (o.PrId is not null && o.Base is not null)))
            throw new SmartWizzException(FailureKind.Usage, "specify exactly one of --base, --pr or --staged");
        if (o.PrId is not null && o.Head != "HEAD")
            throw new SmartWizzException(FailureKind.Usage, "--head cannot be combined with --pr");
        return o;
    }
}
