using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SmartWizz;

public static class FindingStore
{
    // Store only explicit parameters. Never serialize Finding or ReviewResult into SQLite.
    static string Hash(byte[] key, params string[] parts) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));

    public static async Task TryRecordAsync(Options opt, string scope, ReviewResult result, TextWriter log, CancellationToken ct)
    {
        try
        {
            string? encoded = Environment.GetEnvironmentVariable("SMARTWIZZ_HISTORY_KEY");
            if (string.IsNullOrEmpty(encoded))
            {
                log.WriteLine("warning: finding history requires SMARTWIZZ_HISTORY_KEY (32 random bytes, base64)");
                return;
            }
            byte[] key = Convert.FromBase64String(encoded);
            try
            {
                if (key.Length != 32) throw new ArgumentException("invalid history key");
                string root = await GitDiff.RepositoryRootAsync(opt.Repo, ct);
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local)) throw new IOException("local application data unavailable");
                string directory = Path.GetFullPath(Path.Combine(local, "smartwizz", "data"));
                string relative = Path.GetRelativePath(root, directory);
                if (!Path.IsPathRooted(relative) && relative != ".." &&
                    !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new IOException("history must be outside the checkout");
                Directory.CreateDirectory(directory);
                string repository = Hash(key, "repository-v1", OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root);
                string selection = opt.Staged ? "staged" : opt.Check ? "working" : $"pr:{opt.Base}:{opt.Head}:{opt.PrId}";
                string revision = await GitDiff.ResolveShaAsync(opt.Repo, opt.Check || opt.Staged ? "HEAD" : opt.Head, ct);
                ct.ThrowIfCancellationRequested();
                Record(Path.Combine(directory, "findings-v1.db"), key, repository, Hash(key, scope, selection), revision, result, ct);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or FormatException or ArgumentException or SmartWizzException)
        {
            // Do not echo database errors or potentially sensitive paths or data.
            log.WriteLine("warning: finding history unavailable; review results are unaffected");
        }
    }

    public static void Record(string database, byte[] key, string repository, string scope, string revision,
        ReviewResult result, CancellationToken ct = default)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Pooling = false, DefaultTimeout = 5,
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
        List<(Finding Finding, string Anchor)> entries = result.Findings.Where(f => result.FindingAnchors.ContainsKey(f.Fingerprint))
            .Select(f => (Finding: f, Anchor: Hash(key, "anchor-v1", f.FilePath,
                RuleTaxonomy.Canonical(f.RuleId), result.FindingAnchors[f.Fingerprint]))).ToList();
        HashSet<string> repeated = entries.GroupBy(e => e.Anchor).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        int known = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            Finding f = entry.Finding;
            // Ambiguous identical anchors within this run are location-specific.
            string anchor = repeated.Contains(entry.Anchor)
                ? Hash(key, entry.Anchor, f.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : entry.Anchor;
            string id = Hash(key, "finding-v1", repository, scope, anchor);
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
            command.Parameters.AddWithValue("$category", RuleTaxonomy.StorageCategory(f.RuleId));
            command.Parameters.AddWithValue("$path", Hash(key, "path-v1", f.FilePath));
            command.Parameters.AddWithValue("$start", f.StartLine);
            command.Parameters.AddWithValue("$end", f.EndLine ?? f.StartLine);
            command.Parameters.AddWithValue("$severity", (int)f.Severity);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
            ids.Add(f.Fingerprint, id);
        }
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        result.KnownFindings = known;
        foreach ((string fingerprint, string id) in ids) result.PersistentFindingIds.Add(fingerprint, id);
    }
}
