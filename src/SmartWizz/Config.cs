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

        var az = cfg.AzureDevOps;
        az.CollectionUri ??= env("SYSTEM_COLLECTIONURI");
        az.Project ??= env("SYSTEM_TEAMPROJECT");
        az.RepositoryId ??= env("BUILD_REPOSITORY_ID");
        if (az.PullRequestId is null && int.TryParse(env("SYSTEM_PULLREQUEST_PULLREQUESTID"), out var pr))
            az.PullRequestId = pr;
        return cfg;
    }

    static Config Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json) ?? new Config();
        }
        catch (JsonException e)
        {
            throw new SmartWizzException($"invalid config {path}: {e.Message}");
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
}

public sealed class ReviewConfig
{
    public int ContextLines { get; set; } = 10;
    public int MaxCharsPerRequest { get; set; } = 60_000;
    public double MinConfidence { get; set; } = 0.5;
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
public sealed class SmartWizzException(string message) : Exception(message);
