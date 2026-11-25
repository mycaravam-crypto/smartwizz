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

    internal static string PolicyScope(string? rulesetSha, string? guidelines) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new string?[]
        { "history-policy-v2", rulesetSha, guidelines }))));

    public static async Task TryRecordAsync(Options opt, string scope, ReviewResult result, TextWriter log, string? legacyScope, CancellationToken ct)
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
                string? legacy = legacyScope is null ? null : Hash(key, legacyScope, selection);
                if (result.CacheHit)
                {
                    ReadIdentities(database, key, repository, scoped, result, legacy, ct);
                }
                else
                {
                    string revision = await GitDiff.ResolveShaAsync(opt.Repo, opt.Check || opt.Staged ? "HEAD" : opt.Head, ct);
                    ct.ThrowIfCancellationRequested();
                    Record(database, key, repository, scoped, revision, result, legacy, ct);
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

    static string StorageCategory(ReviewResult result, string category) =>
        result.ActiveRuleset?.ContainsRule(category) == true ? category : RuleTaxonomy.StorageCategory(category);

    static string Category(ReviewResult result, Finding finding) =>
        result.FindingCategories.TryGetValue(finding.Fingerprint, out string? category)
            ? category : RuleTaxonomy.Canonical(finding.RuleId);

    static void MergeLegacyAliases(SqliteConnection db, SqliteTransaction tx, byte[] key, string repository,
        string scope, Finding finding, string source, string category, string id, string anchor)
    {
        string[] aliases = category switch
        {
            "sql-injection" => ["std-injection"],
            "off-by-one" => ["index-out-of-range", "array-out-of-bounds"],
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
                    AND NOT EXISTS (SELECT 1 FROM finding_lifecycle WHERE id=$old)
                ON CONFLICT(id) DO UPDATE SET observations=findings.observations+excluded.observations,
                    first_seen=min(findings.first_seen,excluded.first_seen),last_seen=max(findings.last_seen,excluded.last_seen);
                DELETE FROM findings WHERE id=$old AND start_line=$line
                    AND NOT EXISTS (SELECT 1 FROM finding_lifecycle WHERE id=$old);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$anchor", anchor);
            command.Parameters.AddWithValue("$category", RuleTaxonomy.StorageCategory(category));
            command.Parameters.AddWithValue("$old", oldId);
            command.Parameters.AddWithValue("$line", finding.StartLine);
            command.ExecuteNonQuery();
        }
    }

    // Bind a stable policy scope to an existing exact legacy scope only when it
    // can be reconstructed and has records. Keep its original ID namespace.
    static string EffectiveScope(SqliteConnection db, SqliteTransaction? tx, string repository, string scope, string? legacy, bool write)
    {
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$repository", repository);
        command.Parameters.AddWithValue("$scope", scope);
        if (write)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS history_scopes (
                    repository TEXT NOT NULL,
                    policy_scope TEXT NOT NULL,
                    effective_scope TEXT NOT NULL,
                    PRIMARY KEY(repository,policy_scope),
                    UNIQUE(repository,effective_scope)
                );
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='history_scopes'";
        bool hasMapping = command.ExecuteScalar() is not null;
        if (hasMapping)
        {
            command.CommandText = "SELECT effective_scope FROM history_scopes WHERE repository=$repository AND policy_scope=$scope";
            if (command.ExecuteScalar() is string existing) return existing;
        }
        // Prefer an already populated stable namespace over importing a legacy namespace.
        command.CommandText = "SELECT 1 FROM findings WHERE repository=$repository AND scope=$scope LIMIT 1";
        bool hasStable = command.ExecuteScalar() is not null;
        string effective = scope;
        if (!hasStable && legacy is not null)
        {
            command.Parameters.AddWithValue("$legacy", legacy);
            command.CommandText = "SELECT 1 FROM findings WHERE repository=$repository AND scope=$legacy LIMIT 1";
            bool hasLegacy = command.ExecuteScalar() is not null;
            if (hasLegacy && hasMapping)
            {
                command.CommandText = "SELECT 1 FROM history_scopes WHERE repository=$repository AND effective_scope=$legacy";
                hasLegacy = command.ExecuteScalar() is null;
            }
            if (hasLegacy) effective = legacy;
        }
        if (write)
        {
            command.Parameters.AddWithValue("$effective", effective);
            command.CommandText = "INSERT INTO history_scopes(repository,policy_scope,effective_scope) VALUES ($repository,$scope,$effective)";
            command.ExecuteNonQuery();
        }
        return effective;
    }

    internal static void ReadIdentities(string database, byte[] key, string repository, string scope,
        ReviewResult result, CancellationToken ct) => ReadIdentities(database, key, repository, scope, result, null, ct);

    internal static void ReadIdentities(string database, byte[] key, string repository, string scope,
        ReviewResult result, string? legacyScope, CancellationToken ct)
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
        if (legacyScope is not null)
            scope = EffectiveScope(db, null, repository, scope, legacyScope, write: false);
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

    // Explicit human attestation, not an automated semantic verification.
    public static async Task ResolveAttestedAsync(Options opt, string scope, string legacyScope, string id, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("CI") is not null || !opt.Remember)
            throw new SmartWizzException(FailureKind.Usage, "resolution requires enabled local history outside CI");
        try
        {
            string directory = await HistoryKey.SafeDirectoryAsync(opt.Repo, ct);
            byte[] key = HistoryKey.Load(directory, false) ??
                throw new SmartWizzException(FailureKind.Configuration, "existing history key required");
            try
            {
                string root = await GitDiff.RepositoryRootAsync(opt.Repo, ct);
                string repository = Hash(key, "repository-v1", OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root);
                string selection = opt.Staged ? "staged" : "working";
                string revision = await GitDiff.ResolveShaAsync(opt.Repo, "HEAD", ct);
                ResolveAttested(Path.Combine(directory, "findings-v1.db"), repository, Hash(key, scope, selection),
                    Hash(key, legacyScope, selection), id, revision, ct);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new SmartWizzException(FailureKind.Configuration, "finding resolution storage unavailable");
        }
    }

    internal static void ResolveAttested(string database, string repository, string scope, string? legacyScope,
        string id, string revision, CancellationToken ct)
    {
        if (id.Length != 64 || !id.All(Uri.IsHexDigit))
            throw new SmartWizzException(FailureKind.Usage, "resolution requires the full 64-character finding ID from JSON");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        db.Open();
        using SqliteTransaction tx = db.BeginTransaction();
        EnsureLifecycle(db, tx);
        scope = EffectiveScope(db, tx, repository, scope, legacyScope, write: true);
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", id.ToLowerInvariant());
        command.Parameters.AddWithValue("$repository", repository);
        command.Parameters.AddWithValue("$scope", scope);
        command.CommandText = "SELECT 1 FROM findings WHERE id=$id AND repository=$repository AND scope=$scope";
        if (command.ExecuteScalar() is null)
            throw new SmartWizzException(FailureKind.Usage, "finding not found in current history scope");
        command.CommandText = "SELECT state FROM finding_lifecycle WHERE id=$id";
        if (command.ExecuteScalar() is string state && state == "resolved")
            throw new SmartWizzException(FailureKind.Usage, "finding already resolved");
        command.CommandText = "INSERT INTO finding_lifecycle(id,state) VALUES ($id,'resolved') ON CONFLICT(id) DO UPDATE SET state='resolved'";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO finding_lifecycle_audit(id,event,revision,recorded_at) VALUES ($id,'human-attested-resolution',$revision,$now)";
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        ct.ThrowIfCancellationRequested();
        tx.Commit();
    }

    // Separate lifecycle from observation. Existing rows without a state are open.
    // Resolution is not inferred here; a future explicit verifier may record it.
    static void EnsureLifecycle(SqliteConnection db, SqliteTransaction tx)
    {
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS finding_lifecycle (
                id TEXT PRIMARY KEY,
                state TEXT NOT NULL CHECK(state IN ('resolved','regressed'))
            );
            CREATE TABLE IF NOT EXISTS finding_lifecycle_audit (
                sequence INTEGER PRIMARY KEY,
                id TEXT NOT NULL,
                event TEXT NOT NULL CHECK(event IN ('human-attested-resolution','rediscovered')),
                revision TEXT NOT NULL,
                recorded_at INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    internal static string ObserveLifecycle(string prior) => prior == "resolved" || prior == "regressed" ? "regressed" : "open";

    // Evidence of a source anchor, not proof of a defect or its resolution.
    internal static (string Status, int? Line) MatchHistoricalAnchor(byte[] key, FileDiff? file, string category,
        string anchor, ref int budget, CancellationToken ct)
    {
        if (category == "other") return ("not-checked", null);
        if (file is null) return ("not-in-reviewed-diff", null);
        if (file.AddedLines.Count > budget) return ("not-checked", null);
        budget -= file.AddedLines.Count;
        int? match = null;
        foreach (int line in file.AddedLines)
        {
            ct.ThrowIfCancellationRequested();
            if (!file.NewLines.ContainsKey(line)) continue;
            string candidate = Hash(key, "anchor-v1", file.Path, category, Review.SourceAnchor(file, line));
            if (!string.Equals(candidate, anchor, StringComparison.Ordinal)) continue;
            if (match is not null) return ("ambiguous", null);
            match = line;
        }
        return match is null ? ("not-matched", null) : ("exact-match", match);
    }

    public static void Record(string database, byte[] key, string repository, string scope, string revision,
        ReviewResult result, CancellationToken ct = default) => Record(database, key, repository, scope, revision, result, null, ct);

    public static void Record(string database, byte[] key, string repository, string scope, string revision,
        ReviewResult result, string? legacyScope, CancellationToken ct = default)
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
        EnsureLifecycle(db, tx);
        if (legacyScope is not null)
            scope = EffectiveScope(db, tx, repository, scope, legacyScope, write: true);
        var entries = result.Findings.Where(f => result.FindingAnchors.ContainsKey(f.Fingerprint))
            .Select(f => (Finding: f, Anchor: Hash(key, "anchor-v1", f.FilePath,
                Category(result, f), result.FindingAnchors[f.Fingerprint]))).ToList();
        var repeated = entries.GroupBy(e => e.Anchor).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        int known = 0;
        var states = new Dictionary<string, LocalFindingState>(StringComparer.Ordinal);
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
            command.CommandText = "SELECT COALESCE(l.state, 'open') FROM findings f LEFT JOIN finding_lifecycle l ON l.id=f.id WHERE f.id=$id";
            command.Parameters.AddWithValue("$id", id);
            string? prior = command.ExecuteScalar() as string;
            if (prior is not null) known++;
            string lifecycle = ObserveLifecycle(prior ?? "open");
            states.Add(f.Fingerprint, new LocalFindingState(lifecycle, prior is null ? "new" : "known"));
            command.CommandText = """
                INSERT INTO findings (id,repository,scope,anchor,category,path_hash,start_line,end_line,severity,revision,first_seen,last_seen,observations)
                VALUES ($id,$repository,$scope,$anchor,$category,$path,$start,$end,$severity,$revision,$now,$now,1)
                ON CONFLICT(id) DO UPDATE SET start_line=excluded.start_line,end_line=excluded.end_line,
                    category=excluded.category,severity=excluded.severity,revision=excluded.revision,last_seen=excluded.last_seen,observations=observations+1
                """;
            command.Parameters.AddWithValue("$repository", repository);
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$anchor", anchor);
            command.Parameters.AddWithValue("$category", StorageCategory(result, Category(result, f)));
            command.Parameters.AddWithValue("$path", Hash(key, "path-v1", f.FilePath));
            command.Parameters.AddWithValue("$start", f.StartLine);
            command.Parameters.AddWithValue("$end", f.EndLine ?? f.StartLine);
            command.Parameters.AddWithValue("$severity", (int)f.Severity);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
            if (lifecycle == "regressed")
            {
                command.CommandText = "INSERT INTO finding_lifecycle(id,state) VALUES ($id,'regressed') ON CONFLICT(id) DO UPDATE SET state='regressed'";
                command.ExecuteNonQuery();
                if (prior == "resolved")
                {
                    command.CommandText = "INSERT INTO finding_lifecycle_audit(id,event,revision,recorded_at) VALUES ($id,'rediscovered',$revision,$now)";
                    command.ExecuteNonQuery();
                }
            }
            ids.Add(f.Fingerprint, id);
        }
        // Historical omissions are a report projection, not a stored resolution transition.
        // Include all records in this exact checkout/scope, even when the file was not reviewed.
        List<HistoricalFinding> unverified = [];
        List<HistoricalRevalidation.Candidate> candidates = [];
        var observed = ids.Values.ToHashSet(StringComparer.Ordinal);
        var reviewedPaths = result.ReviewedSourceFiles.ToDictionary(f => Hash(key, "path-v1", f.Path), StringComparer.Ordinal);
        int anchorBudget = 10_000;
        using (SqliteCommand history = db.CreateCommand())
        {
            history.Transaction = tx;
            history.CommandText = """
                SELECT f.id,category,severity,start_line,last_seen,observations,path_hash,anchor,COALESCE(l.state,'open')
                FROM findings f LEFT JOIN finding_lifecycle l ON l.id=f.id
                WHERE repository=$repository AND scope=$scope ORDER BY last_seen DESC,f.id
                """;
            history.Parameters.AddWithValue("$repository", repository);
            history.Parameters.AddWithValue("$scope", scope);
            using SqliteDataReader rows = history.ExecuteReader();
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                string id = rows.GetString(0);
                if (observed.Contains(id) || rows.GetString(8) == "resolved") continue;
                string category = StorageCategory(result, rows.GetString(1));
                reviewedPaths.TryGetValue(rows.GetString(6), out FileDiff? file);
                (string status, int? line) = MatchHistoricalAnchor(key, file, category, rows.GetString(7), ref anchorBudget, ct);
                string pattern = status == "exact-match" && line is { } matched && file is not null &&
                    file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? RuleTaxonomy.HistoricalPattern(category, file.NewLines[matched]) : "not-checked";
                if (result.CollectHistoricalCandidates && status == "exact-match" && line is { } candidateLine && file is not null &&
                    HistoricalRevalidation.CreateCandidate(id, (Severity)rows.GetInt32(2), category, file, candidateLine) is { } candidate)
                {
                    candidates.Add(candidate);
                    if (candidates.Count > HistoricalRevalidation.MaxCandidates)
                        candidates = [.. candidates.OrderByDescending(c => c.Severity).ThenBy(c => c.Id, StringComparer.Ordinal)
                            .Take(HistoricalRevalidation.MaxCandidates)];
                }
                unverified.Add(new HistoricalFinding(id, category,
                    (Severity)rows.GetInt32(2), rows.GetInt32(3), rows.GetInt64(4), rows.GetInt64(5), status, line, rows.GetString(8), pattern));
            }
        }
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        result.UnverifiedHistory = unverified;
        result.HistoricalCandidates.AddRange(candidates);
        foreach ((string fingerprint, LocalFindingState state) in states) result.LocalFindingStates.Add(fingerprint, state);
        result.KnownFindings = known;
        foreach ((string fingerprint, string id) in ids) result.PersistentFindingIds.Add(fingerprint, id);
    }
}
