using System.Runtime.InteropServices;

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
        Config.LoadDotEnv(".env");
        var cfg = Config.Load(opt.ConfigPath, Environment.GetEnvironmentVariable);
        if (Uri.TryCreate(cfg.Provider.Endpoint, UriKind.Absolute, out Uri? endpoint) && endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
            Console.Error.WriteLine("warning: provider endpoint uses plain HTTP; code and API key are sent unencrypted");
        List<FileDiff> files = GitDiff.Parse(await GitDiff.RunAsync(opt.Repo, opt.Base!, opt.Head, cfg.Review.ContextLines, ct));

        // No client-wide timeout: Provider and AzureDevOpsPublisher bound every request with their configured timeout.
        using HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
        Provider provider = new(http, cfg.Provider, Environment.GetEnvironmentVariable("SMARTWIZZ_API_KEY"), log: Console.Error);
        string? standard = cfg.Review.Guidelines is { } g ? await File.ReadAllTextAsync(g, ct) : null;
        ReviewResult result = await Review.RunAsync(files, cfg.Review, provider, Review.SystemPrompt(LoadPrompt(), standard), ct);

        Report.WriteConsole(result, Console.Out);
        if (opt.JsonPath is not null)
            await File.WriteAllTextAsync(opt.JsonPath, Report.ToJson(result), ct);

        if (opt.Publish)
        {
            string? token = Environment.GetEnvironmentVariable("SMARTWIZZ_AZDO_TOKEN") ?? Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");
            (int posted, int present) = await new AzureDevOpsPublisher(http, cfg.AzureDevOps, token).PublishAsync(result.Findings, ct);
            Console.WriteLine($"\nPublished {posted} new thread(s); {present} already present.");
        }

        return Review.ExitCode(result.Findings, opt.FailOn);
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
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new SmartWizzException(FailureKind.Usage, $"{args[i - 1]} needs a value");
            o = args[i] switch
            {
                "--base" => o with { Base = Value() },
                "--head" => o with { Head = Value() },
                "--repo" => o with { Repo = Value() },
                "--config" => o with { ConfigPath = Value() },
                "--json" => o with { JsonPath = Value() },
                "--fail-on" => o with { FailOn = Review.ParseSeverity(Value()) ?? throw new SmartWizzException(FailureKind.Usage, $"invalid severity: {args[i]}") },
                "--publish" => o with { Publish = true },
                "-h" or "--help" => o with { Help = true },
                _ => throw new SmartWizzException(FailureKind.Usage, $"unknown argument: {args[i]}"),
            };
        }
        if (!o.Help && string.IsNullOrWhiteSpace(o.Base))
            throw new SmartWizzException(FailureKind.Usage, "--base is required");
        return o;
    }
}
