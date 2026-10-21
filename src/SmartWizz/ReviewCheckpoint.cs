using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SmartWizz;

[JsonConverter(typeof(JsonStringEnumConverter<FindingStatus>))]
public enum FindingStatus
{
    [JsonStringEnumMemberName("open")] Open,
    [JsonStringEnumMemberName("resolved")] Resolved,
    // Reported again after an earlier verified resolution.
    [JsonStringEnumMemberName("regressed")] Regressed,
}

public sealed record TrackedFinding(Finding Finding, FindingStatus Status, string FirstSeenSha, string LastCheckedSha, string? Resolution = null)
{
    [JsonIgnore]
    public bool IsUnresolved => Status != FindingStatus.Resolved;
}

// Result of re-checking a previously open finding against the current code.
public sealed record Recheck(string Fingerprint, bool Resolved, string Note, string CurrentPath);

// Versioned review checkpoint. Never treat absence from a delta review as resolution: only a Recheck resolves.
public sealed partial class ReviewCheckpoint
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string HeadSha { get; set; } = "";
    public string? ReviewScopeSha256 { get; set; }
    public List<TrackedFinding> Findings { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<Finding> UnresolvedFindings => Findings.Where(t => t.IsUnresolved).Select(t => t.Finding).ToList();

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<Severity>() },
        WriteIndented = true,
    };

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex CommitSha();

    public static bool IsCommitSha(string? value) => value is not null && CommitSha().IsMatch(value);

    // Missing, corrupt or incompatible state is not an error: the caller falls back to a complete review.
    public static ReviewCheckpoint? Load(string? path, TextWriter log)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            ReviewCheckpoint? state = JsonSerializer.Deserialize<ReviewCheckpoint>(File.ReadAllText(path), Json);
            if (state is null || state.Version != CurrentVersion || !IsCommitSha(state.HeadSha) || state.Findings is null
                || state.Findings.Any(t => t?.Finding is null || string.IsNullOrWhiteSpace(t.Finding.Fingerprint)
                                           || string.IsNullOrWhiteSpace(t.Finding.FilePath)
                                           || !Enum.IsDefined(t.Status) || !Enum.IsDefined(t.Finding.Severity))
                || state.Findings.DistinctBy(t => t.Finding.Fingerprint, StringComparer.Ordinal).Count() != state.Findings.Count)
                throw new InvalidDataException("unsupported version or invalid content");
            return state;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or NotSupportedException)
        {
            log.WriteLine($"warning: ignoring review checkpoint ({e.Message}); running a complete review");
            return null;
        }
    }

    // Pure lifecycle transition from the previous checkpoint, this run's findings and the rechecks of earlier findings.
    public static ReviewCheckpoint Advance(string headSha, string? reviewScopeSha256, ReviewCheckpoint? previous,
        IEnumerable<Finding> newFindings, IEnumerable<Recheck> rechecks)
    {
        if (!IsCommitSha(headSha))
            throw new SmartWizzException(FailureKind.Usage, "checkpoint head must be a full commit SHA");

        var earlier = (previous?.Findings ?? []).ToDictionary(t => t.Finding.Fingerprint, StringComparer.Ordinal);
        var checks = rechecks.ToDictionary(r => r.Fingerprint, StringComparer.Ordinal);
        List<TrackedFinding> next = [];
        HashSet<string> reported = new(StringComparer.Ordinal);

        foreach (Finding found in Review.Dedupe(newFindings))
        {
            reported.Add(found.Fingerprint);
            if (!earlier.TryGetValue(found.Fingerprint, out TrackedFinding? before))
            {
                next.Add(new TrackedFinding(found, FindingStatus.Open, headSha, headSha));
                continue;
            }
            Finding strongest = Review.Dedupe([before.Finding, found]).Single();
            FindingStatus status = before.Status == FindingStatus.Resolved ? FindingStatus.Regressed : before.Status;
            next.Add(new TrackedFinding(strongest, status, before.FirstSeenSha, headSha));
        }

        foreach (TrackedFinding before in earlier.Values.Where(t => !reported.Contains(t.Finding.Fingerprint)))
        {
            if (!before.IsUnresolved || !checks.TryGetValue(before.Finding.Fingerprint, out Recheck? check))
            {
                next.Add(before);
                continue;
            }
            Finding moved = before.Finding with { FilePath = check.CurrentPath };
            next.Add(check.Resolved
                ? new TrackedFinding(moved, FindingStatus.Resolved, before.FirstSeenSha, headSha, check.Note)
                : before with { Finding = moved, LastCheckedSha = headSha });
        }

        return new ReviewCheckpoint
        {
            HeadSha = headSha,
            ReviewScopeSha256 = reviewScopeSha256,
            Findings = [.. next.OrderBy(t => t.Status == FindingStatus.Resolved)
                .ThenByDescending(t => t.Finding.Severity)
                .ThenBy(t => t.Finding.FilePath, StringComparer.Ordinal)
                .ThenBy(t => t.Finding.StartLine)],
        };
    }

    public void Save(string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
