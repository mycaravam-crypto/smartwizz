using System.Text.Json;

namespace SmartWizz;

// No secret fields on purpose: credentials come only from environment variables.
public sealed class Config
{
    public ProviderConfig Provider { get; set; } = new();
    public ReviewConfig Review { get; set; } = new();
    public AzureDevOpsConfig AzureDevOps { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Config Load(string? path, Func<string, string?> env)
    {
        Config cfg;
        if (path is null)
            cfg = File.Exists("smartwizz.json") ? Read("smartwizz.json") : new Config();
        else if (File.Exists(path))
            cfg = Read(path);
        else
            throw new SmartWizzException($"config file not found: {path}");

        // Relative to the config file, so CI resolves it inside the trusted SmartWizz checkout, not the PR.
        if (cfg.Review.Guidelines is { } g)
        {
            cfg.Review.Guidelines = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path ?? "smartwizz.json"))!, g);
            if (!File.Exists(cfg.Review.Guidelines))
                throw new SmartWizzException($"guidelines file not found: {cfg.Review.Guidelines}");
        }

        // Lets CI point at an internal endpoint/model without a separate config file.
        // Empty counts as unset: CI systems pass unset inputs as empty strings.
        if (env("SMARTWIZZ_ENDPOINT") is { Length: > 0 } endpoint) cfg.Provider.Endpoint = endpoint;
        if (env("SMARTWIZZ_MODEL") is { Length: > 0 } model) cfg.Provider.Model = model;

        var az = cfg.AzureDevOps;
        az.CollectionUri ??= env("SYSTEM_COLLECTIONURI");
        az.Project ??= env("SYSTEM_TEAMPROJECT");
        az.RepositoryId ??= env("BUILD_REPOSITORY_ID");
        if (az.PullRequestId is null && int.TryParse(env("SYSTEM_PULLREQUEST_PULLREQUESTID"), out var pr))
            az.PullRequestId = pr;
        return cfg;
    }

    // Local convenience for secrets (KEY=value lines). Never overrides variables already set, so CI secrets win.
    public static void LoadDotEnv(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (line.StartsWith('#') || eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"', '\'');
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    static Config Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json) ?? new Config();
        }
        catch (JsonException e)
        {
            throw new SmartWizzException($"invalid config {path}: {e.Message}", e);
        }
    }
}

public sealed class ProviderConfig
{
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    // null = omit (some reasoning models reject a temperature parameter)
    public double? Temperature { get; set; } = 0;
    public int TimeoutSeconds { get; set; } = 180;
    // null = "Authorization: Bearer <key>"; otherwise the raw key in this header (e.g. "api-key" for Azure OpenAI).
    public string? ApiKeyHeader { get; set; }
}

public sealed class ReviewConfig
{
    public int ContextLines { get; set; } = 10;
    public int MaxCharsPerRequest { get; set; } = 60_000;
    public double MinConfidence { get; set; } = 0.5;
    // Optional markdown engineering standard checked in addition to bugs/security/tests.
    public string? Guidelines { get; set; }
    public string[] Exclude { get; set; } =
    [
        "*.lock", "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "packages.lock.json",
        "*.min.js", "*.min.css", "*.map", "*.svg",
        "*.g.cs", "*.designer.cs", "*.Designer.cs", "*/Migrations/*",
    ];
}

public sealed class AzureDevOpsConfig
{
    public string? CollectionUri { get; set; }
    public string? Project { get; set; }
    public string? RepositoryId { get; set; }
    public int? PullRequestId { get; set; }
    public string ApiVersion { get; set; } = "6.0";
}

// Expected execution failures: printed without a stack trace, exit code 2.
public sealed class SmartWizzException(string message, Exception? innerException = null) : Exception(message, innerException);
