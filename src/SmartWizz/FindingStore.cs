using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SmartWizz;

public static class FindingStore
{
    // Store only explicit parameters. Never serialize Finding or ReviewResult into SQLite.
    internal static string Hash(byte[] key, params string[] parts) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));

    public static async Task TryRecordAsync(Options opt, string scope, ReviewResult result, TextWriter log, CancellationToken ct)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            string directory = await HistoryKey.SafeDirectoryAsync(opt.Repo, ct);
            byte[]? key = HistoryKey.Load(directory, opt.InitializeHistory);
            if (key is null)
            {
                log.WriteLine("warning: protected history key unavailable; review results are unaffected");
                return;
            }
            try
            {
                string root = await GitDiff.RepositoryRootAsync(opt.Repo, ct);
                Directory.CreateDirectory(directory);
                string repository = Hash(key, "repository-v1", OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root);
                string selection = opt.Staged ? "staged" : opt.Check ? "working" : $"pr:{opt.Base}:{opt.Head}:{opt.PrId}";
                string database = Path.Combine(directory, "findings-v1.db");
                string scoped = Hash(key, scope, selection);
                if (result.CacheHit)
                {
                    ReadIdentities(database, key, repository, scoped, result, ct);
                }
                else
                {
                    string revision = await GitDiff.ResolveShaAsync(opt.Repo, opt.Check || opt.Staged ? "HEAD" : opt.Head, ct);
                    ct.ThrowIfCancellationRequested();
                    Record(database, key, repository, scoped, revision, result, ct);
                }
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or FormatException or ArgumentException or SmartWizzException or CryptographicException or PlatformNotSupportedException)
        {
            // Do not echo database errors or potentially sensitive paths or data.
            log.WriteLine("warning: finding history unavailable; review results are unaffected");
        }
        finally { result.HistoryElapsedMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    static string Category(ReviewResult result, Finding finding) =>
        result.FindingCategories.TryGetValue(finding.Fingerprint, out string? category)
            ? category : RuleTaxonomy.Canonical(finding.RuleId);

    static void MergeLegacyAliases(SqliteConnection db, SqliteTransaction tx, byte[] key, string repository,
        string scope, Finding finding, string source, string category, string id, string anchor)
    {
        string[] aliases = category switch
        {
            "sql-injection" => ["std-injection"],
            "off-by-one" => ["index-out-of-range"],
            "hardcoded-credential" => ["std-hardcoded-config", "hardcoded-config"],
            _ => [],
        };
        foreach (string alias in aliases)
        {
            // Do not reinterpret an unrelated broad standard check without evidence.
            string[] lines = source.Split('\n');
            string line = lines[finding.StartLine == 1 ? 0 : Math.Min(1, lines.Length - 1)];
            if (RuleTaxonomy.Canonical(alias, line) != category) continue;
            string oldAnchor = Hash(key, "anchor-v1", finding.FilePath, alias, source);
            string oldId = Hash(key, "finding-v1", repository, scope, oldAnchor);
            using SqliteCommand command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO findings (id,repository,scope,anchor,category,path_hash,start_line,end_line,severity,revision,first_seen,last_seen,observations)
                SELECT $id,repository,scope,$anchor,$category,path_hash,start_line,end_line,severity,revision,first_seen,last_seen,observations
                FROM findings WHERE id=$old AND start_line=$line
                ON CONFLICT(id) DO UPDATE SET observations=findings.observations+excluded.observations,
                    first_seen=min(findings.first_seen,excluded.first_seen),last_seen=max(findings.last_seen,excluded.last_seen);
                DELETE FROM findings WHERE id=$old AND start_line=$line;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$anchor", anchor);
            command.Parameters.AddWithValue("$category", RuleTaxonomy.StorageCategory(category));
            command.Parameters.AddWithValue("$old", oldId);
            command.Parameters.AddWithValue("$line", finding.StartLine);
            command.ExecuteNonQuery();
        }
    }

    internal static void ReadIdentities(string database, byte[] key, string repository, string scope,
        ReviewResult result, CancellationToken ct)
    {
        if (key.Length != 32) throw new ArgumentException("invalid history key", nameof(key));
        if (!File.Exists(database)) return;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        db.Open();
        var entries = result.Findings.Where(f => result.FindingAnchors.ContainsKey(f.Fingerprint))
            .Select(f => (Finding: f, Anchor: Hash(key, "anchor-v1", f.FilePath,
                Category(result, f), result.FindingAnchors[f.Fingerprint]))).ToList();
        var repeated = entries.GroupBy(e => e.Anchor).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((Finding finding, string sourceAnchor) in entries)
        {
            ct.ThrowIfCancellationRequested();
            string anchor = repeated.Contains(sourceAnchor)
                ? Hash(key, sourceAnchor, finding.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : sourceAnchor;
            string id = Hash(key, "finding-v1", repository, scope, anchor);
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = "SELECT id FROM findings WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteScalar() is string storedId) ids.Add(finding.Fingerprint, storedId);
        }
        foreach ((string fingerprint, string id) in ids) result.PersistentFindingIds.Add(fingerprint, id);
        result.KnownFindings = ids.Count;
        // A cached snapshot is not a new inspection; do not infer missing findings from it.
    }

    public static void Record(string database, byte[] key, string repository, string scope, string revision,
        ReviewResult result, CancellationToken ct = default)
    {
        if (key.Length != 32) throw new ArgumentException("invalid history key", nameof(key));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        db.Open();
        using SqliteTransaction tx = db.BeginTransaction();
        using (SqliteCommand schema = db.CreateCommand())
        {
            schema.Transaction = tx;
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS findings (
                    id TEXT PRIMARY KEY,
                    repository TEXT NOT NULL,
                    scope TEXT NOT NULL,
                    anchor TEXT NOT NULL,
                    category TEXT NOT NULL,
                    path_hash TEXT NOT NULL,
                    start_line INTEGER NOT NULL,
                    end_line INTEGER NOT NULL,
                    severity INTEGER NOT NULL,
                    revision TEXT NOT NULL,
                    first_seen INTEGER NOT NULL,
                    last_seen INTEGER NOT NULL,
                    observations INTEGER NOT NULL,
                    UNIQUE(repository, scope, anchor)
                );
                """;
            schema.ExecuteNonQuery();
        }
        var entries = result.Findings.Where(f => result.FindingAnchors.ContainsKey(f.Fingerprint))
            .Select(f => (Finding: f, Anchor: Hash(key, "anchor-v1", f.FilePath,
                Category(result, f), result.FindingAnchors[f.Fingerprint]))).ToList();
        var repeated = entries.GroupBy(e => e.Anchor).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        int known = 0;
        foreach ((Finding finding, string sourceAnchor) in entries)
        {
            ct.ThrowIfCancellationRequested();
            Finding f = finding;
            // Ambiguous identical anchors within this run are location-specific.
            string anchor = repeated.Contains(sourceAnchor)
                ? Hash(key, sourceAnchor, f.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : sourceAnchor;
            string id = Hash(key, "finding-v1", repository, scope, anchor);
            // Migrate only proven aliases at the same source anchor and location.
            if (!repeated.Contains(sourceAnchor))
                MergeLegacyAliases(db, tx, key, repository, scope, f, result.FindingAnchors[f.Fingerprint],
                    Category(result, f), id, anchor);
            using SqliteCommand command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT observations FROM findings WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteScalar() is not null) known++;
            command.CommandText = """
                INSERT INTO findings (id,repository,scope,anchor,category,path_hash,start_line,end_line,severity,revision,first_seen,last_seen,observations)
                VALUES ($id,$repository,$scope,$anchor,$category,$path,$start,$end,$severity,$revision,$now,$now,1)
                ON CONFLICT(id) DO UPDATE SET start_line=excluded.start_line,end_line=excluded.end_line,
                    severity=excluded.severity,revision=excluded.revision,last_seen=excluded.last_seen,observations=observations+1
                """;
            command.Parameters.AddWithValue("$repository", repository);
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$anchor", anchor);
            command.Parameters.AddWithValue("$category", RuleTaxonomy.StorageCategory(Category(result, f)));
            command.Parameters.AddWithValue("$path", Hash(key, "path-v1", f.FilePath));
            command.Parameters.AddWithValue("$start", f.StartLine);
            command.Parameters.AddWithValue("$end", f.EndLine ?? f.StartLine);
            command.Parameters.AddWithValue("$severity", (int)f.Severity);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
            ids.Add(f.Fingerprint, id);
        }
        // Historical omissions are a report projection, not a stored resolution transition.
        // Include all records in this exact checkout/scope, even when the file was not reviewed.
        List<HistoricalFinding> unverified = [];
        var observed = ids.Values.ToHashSet(StringComparer.Ordinal);
        using (SqliteCommand history = db.CreateCommand())
        {
            history.Transaction = tx;
            history.CommandText = """
                SELECT id,category,severity,start_line,last_seen,observations FROM findings
                WHERE repository=$repository AND scope=$scope ORDER BY last_seen DESC,id
                """;
            history.Parameters.AddWithValue("$repository", repository);
            history.Parameters.AddWithValue("$scope", scope);
            using SqliteDataReader rows = history.ExecuteReader();
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                string id = rows.GetString(0);
                if (observed.Contains(id)) continue;
                unverified.Add(new HistoricalFinding(id, RuleTaxonomy.StorageCategory(rows.GetString(1)),
                    (Severity)rows.GetInt32(2), rows.GetInt32(3), rows.GetInt64(4), rows.GetInt64(5)));
            }
        }
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        result.UnverifiedHistory = unverified;
        result.KnownFindings = known;
        foreach ((string fingerprint, string id) in ids) result.PersistentFindingIds.Add(fingerprint, id);
    }
}
