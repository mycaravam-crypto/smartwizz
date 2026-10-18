namespace SmartWizz;

public static class Program
{
    const string Usage = """
        Usage: smartwizz --base <ref> [options]

          --base <ref>        Target branch/commit the PR merges into (required), e.g. origin/main
          --head <ref>        PR head (default: HEAD)
          --repo <path>       Git checkout (default: current directory)
          --config <path>     Config file (default: ./smartwizz.json if present)
          --json <path>       Also write the JSON report to this file
          --fail-on <sev>     Exit 1 if any finding is at/above low|medium|high|critical
          --publish           Post findings as Azure DevOps PR threads
          -h, --help          Show this help

        Environment: SMARTWIZZ_API_KEY (provider), SMARTWIZZ_AZDO_TOKEN or SYSTEM_ACCESSTOKEN (Azure DevOps).
        Exit codes: 0 ok, 1 findings at/above --fail-on, 2 execution failure.
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
            Console.Error.WriteLine($"error: {e.Message}\n\n{Usage}");
            return 2;
        }
        if (opt.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            Config.LoadDotEnv(".env");
            Config cfg = Config.Load(opt.ConfigPath, Environment.GetEnvironmentVariable);
            List<FileDiff> files = GitDiff.Parse(await GitDiff.RunAsync(opt.Repo, opt.Base!, opt.Head, cfg.Review.ContextLines, CancellationToken.None));

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // per-request timeouts instead
            Provider provider = new(http, cfg.Provider, Environment.GetEnvironmentVariable("SMARTWIZZ_API_KEY"));
            ReviewResult result = await Review.RunAsync(files, cfg.Review, provider,
                Review.SystemPrompt(LoadPrompt(), cfg.Review.Guidelines is { } g ? await File.ReadAllTextAsync(g) : null),
                CancellationToken.None);

            Report.WriteConsole(result, Console.Out);
            if (opt.JsonPath is not null)
                await File.WriteAllTextAsync(opt.JsonPath, Report.ToJson(result));

            if (opt.Publish)
            {
                var token = Environment.GetEnvironmentVariable("SMARTWIZZ_AZDO_TOKEN") ?? Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");
                var (posted, present) = await new AzureDevOpsPublisher(http, cfg.AzureDevOps, token)
                    .PublishAsync(result.Findings, CancellationToken.None);
                Console.WriteLine($"\nPublished {posted} new thread(s); {present} already present.");
            }

            return Review.ExitCode(result.Findings, opt.FailOn);
        }
        catch (Exception e)
        {
            // Messages never include credentials: tokens live only in request headers.
            Console.Error.WriteLine($"error: {e.Message}");
            return 2;
        }
    }

    static string LoadPrompt()
    {
        using Stream stream = typeof(Program).Assembly.GetManifestResourceStream("code-review.md")
            ?? throw new InvalidOperationException("embedded prompt code-review.md is missing from the build");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}

public sealed record Options
{
    public string? Base { get; init; }
    public string Head { get; init; } = "HEAD";
    public string Repo { get; init; } = ".";
    public string? ConfigPath { get; init; }
    public string? JsonPath { get; init; }
    public Severity? FailOn { get; init; }
    public bool Publish { get; init; }
    public bool Help { get; init; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new SmartWizzException($"{args[i - 1]} needs a value");
            o = args[i] switch
            {
                "--base" => o with { Base = Value() },
                "--head" => o with { Head = Value() },
                "--repo" => o with { Repo = Value() },
                "--config" => o with { ConfigPath = Value() },
                "--json" => o with { JsonPath = Value() },
                "--fail-on" => o with { FailOn = Review.ParseSeverity(Value()) ?? throw new SmartWizzException($"invalid severity: {args[i]}") },
                "--publish" => o with { Publish = true },
                "-h" or "--help" => o with { Help = true },
                _ => throw new SmartWizzException($"unknown argument: {args[i]}"),
            };
        }
        if (!o.Help && string.IsNullOrWhiteSpace(o.Base))
            throw new SmartWizzException("--base is required");
        return o;
    }
}
