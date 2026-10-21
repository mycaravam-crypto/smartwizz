using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartWizz;

// Versioned review checkpoint. Never treat absence from a delta review as resolution.
public sealed class ReviewCheckpoint
{
    public int Version { get; set; } = 1;
    public string HeadSha { get; set; } = "";
    public string? ReviewScopeSha256 { get; set; }
    public List<Finding> OpenFindings { get; set; } = [];

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<Severity>() },
        WriteIndented = true,
    };

    public static ReviewCheckpoint? Load(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            ReviewCheckpoint? state = JsonSerializer.Deserialize<ReviewCheckpoint>(File.ReadAllText(path), Json);
            if (state is null || state.Version != 1 || !System.Text.RegularExpressions.Regex.IsMatch(state.HeadSha, "^[0-9a-fA-F]{40}$")
                || state.OpenFindings is null || state.OpenFindings.Any(f => f is null || string.IsNullOrWhiteSpace(f.Fingerprint)))
                throw new InvalidDataException("invalid checkpoint");
            return state;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            throw new SmartWizzException(FailureKind.Configuration, "invalid review checkpoint (run a full review to recreate it)", e);
        }
    }

    // Carry unresolved issues forward; refresh findings repeated in the delta. Never silently resolve.
    public static ReviewCheckpoint Merge(string headSha, ReviewCheckpoint? previous, IEnumerable<Finding> newFindings, string? reviewScopeSha256 = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(headSha, "^[0-9a-fA-F]{40}$"))
            throw new SmartWizzException(FailureKind.Usage, "checkpoint head must be a full commit SHA");
        List<Finding> merged = Review.Dedupe((previous?.OpenFindings ?? []).Concat(newFindings));
        return new ReviewCheckpoint { HeadSha = headSha, ReviewScopeSha256 = reviewScopeSha256, OpenFindings = merged };
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
