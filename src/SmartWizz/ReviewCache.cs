using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SmartWizz;

// An exact-input, opt-in local review cache. No source, paths, model text or
// generated fixes are persisted. A hit is a historical result, not a new review.
internal sealed class ReviewCache(string database, byte[] key, string cacheKey) : IDisposable
{
    internal sealed record Item(int File, int Start, int? End, string Rule, Severity Severity, double Confidence);
    internal sealed record Snapshot(int Reviewed, int Requests, int Detections, Item[] Items);
    internal const int LifetimeSeconds = 24 * 60 * 60;

    public static async Task<ReviewCache?> OpenAsync(Options opt, string scope, string diff, CancellationToken ct)
    {
        string directory = await HistoryKey.SafeDirectoryAsync(opt.Repo, ct);
        byte[]? key = HistoryKey.Load(directory, opt.InitializeHistory);
        if (key is null) return null;
        try
        {
            string root = await GitDiff.RepositoryRootAsync(opt.Repo, ct);
            string revision = await GitDiff.ResolveShaAsync(opt.Repo, "HEAD", ct);
            string identity = FindingStore.Hash(key, "review-cache-v1", typeof(ReviewCache).Module.ModuleVersionId.ToString(),
                root, scope, revision, opt.Staged ? "staged" : "working", diff);
            Directory.CreateDirectory(directory);
            // Own a separate copy; the temporary key is cleared in all cases.
            return new ReviewCache(Path.Combine(directory, "review-cache-v1.db"), (byte[])key.Clone(), identity);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        try
        {
            db.Open();
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS review_cache (
                    cache_key TEXT PRIMARY KEY,
                    created INTEGER NOT NULL,
                    metadata TEXT NOT NULL,
                    signature TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    string Sign(long created, string metadata) =>
        FindingStore.Hash(key, "cache-signature-v1", cacheKey, created.ToString(CultureInfo.InvariantCulture), metadata);

    public ReviewResult? Read(List<FileDiff> files, ReviewConfig cfg, Ruleset? ruleset, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT created,metadata,signature FROM review_cache WHERE cache_key=$key";
        command.Parameters.AddWithValue("$key", cacheKey);
        using SqliteDataReader row = command.ExecuteReader();
        if (!row.Read()) return null;
        long created = row.GetInt64(0);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (created > now || created < now - LifetimeSeconds) return null;
        string metadata = row.GetString(1);
        string signature = row.GetString(2);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature), Convert.FromHexString(Sign(created, metadata)))) return null;
        Snapshot? snapshot = JsonSerializer.Deserialize<Snapshot>(metadata);
        if (snapshot is null || snapshot.Items is null || snapshot.Reviewed != files.Count || snapshot.Requests < 1 ||
            snapshot.Detections < snapshot.Items.Length) return null;
        var result = new ReviewResult
        {
            ReviewedFiles = snapshot.Reviewed,
            CachedRequests = snapshot.Requests,
            CacheHit = true,
            CacheCreatedUnixSeconds = created,
            RawDetections = snapshot.Detections,
            MaxParallelRequests = cfg.MaxParallelRequests,
            RulesetName = ruleset?.Name,
            RulesetVersion = ruleset?.Version,
            RulesetSha256 = ruleset?.Sha256,
        };
        result.Models.Add(model);
        foreach (Item item in snapshot.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.File < 0 || item.File >= files.Count || !RuleTaxonomy.IsCacheable(item.Rule) ||
                !Enum.IsDefined(item.Severity) || !double.IsFinite(item.Confidence) || item.Confidence < cfg.MinConfidence || item.Confidence > 1)
                return null;
            FileDiff file = files[item.File];
            if (!file.AddedLines.Contains(item.Start) || !file.NewLines.TryGetValue(item.Start, out string? line)) return null;
            if (item.End is { } end && (end < item.Start || end - (long)item.Start > file.NewLines.Count ||
                !Enumerable.Range(item.Start, end - item.Start + 1).All(file.NewLines.ContainsKey))) return null;
            string fingerprint = Review.Fingerprint(item.Rule, file.Path, line);
            // A legacy collision cannot be reconstructed without additional provenance. Re-review instead.
            if (result.FindingAnchors.ContainsKey(fingerprint)) return null;
            string category = RuleTaxonomy.Canonical(item.Rule, line);
            result.Findings.Add(new Finding(item.Rule, item.Severity, category,
                "Cached finding metadata from an earlier successful inspection; rerun with --no-cache for details.",
                file.Path, item.Start, item.End, item.Confidence, fingerprint));
            result.FindingAnchors.Add(fingerprint, Review.SourceAnchor(file, item.Start));
            result.FindingCategories.Add(fingerprint, category);
        }
        return result;
    }

    public void Invalidate(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(database)) return;
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "DELETE FROM review_cache WHERE cache_key=$key";
        command.Parameters.AddWithValue("$key", cacheKey);
        command.ExecuteNonQuery();
    }

    public void Write(List<FileDiff> files, ReviewResult result, string model, CancellationToken ct)
    {
        // Do not cache partial/rejected reviews, fallback model results or arbitrary rule strings.
        if (result.Rejected.Count != 0 || result.SkippedFiles.Count != 0 || result.ReviewedFiles != files.Count ||
            result.Requests == 0 || result.Models.Count != 1 || result.Models[0] != model) return;
        List<Item> items = [];
        foreach (Finding finding in result.Findings)
        {
            int index = files.FindIndex(f => f.Path == finding.FilePath);
            if (index < 0 || !RuleTaxonomy.IsCacheable(finding.RuleId) ||
                !files[index].NewLines.TryGetValue(finding.StartLine, out string? line) ||
                Review.Fingerprint(finding.RuleId, finding.FilePath, line) != finding.Fingerprint) return;
            items.Add(new Item(index, finding.StartLine, finding.EndLine, finding.RuleId, finding.Severity, finding.Confidence));
        }
        ct.ThrowIfCancellationRequested();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string metadata = JsonSerializer.Serialize(new Snapshot(result.ReviewedFiles, result.Requests, result.RawDetections, [.. items]));
        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction();
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            DELETE FROM review_cache WHERE created < $expiry;
            INSERT INTO review_cache(cache_key,created,metadata,signature) VALUES ($key,$created,$metadata,$signature)
            ON CONFLICT(cache_key) DO UPDATE SET created=excluded.created,metadata=excluded.metadata,signature=excluded.signature;
            """;
        command.Parameters.AddWithValue("$expiry", created - LifetimeSeconds);
        command.Parameters.AddWithValue("$key", cacheKey);
        command.Parameters.AddWithValue("$created", created);
        command.Parameters.AddWithValue("$metadata", metadata);
        command.Parameters.AddWithValue("$signature", Sign(created, metadata));
        command.ExecuteNonQuery();
        ct.ThrowIfCancellationRequested();
        tx.Commit();
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}
