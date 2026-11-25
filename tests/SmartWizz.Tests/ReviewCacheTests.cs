namespace SmartWizz.Tests;

public class ReviewCacheTests
{
    [Fact]
    public void Exact_cache_roundtrip_preserves_severity_and_excludes_sensitive_text()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(directory, "cache.db");
        try
        {
            List<FileDiff> files = [new FileDiff { Path = "PRIVATE_FILE.cs" }];
            files[0].AddedLines.Add(1);
            files[0].NewLines.Add(1, "PRIVATE_SOURCE_SENTINEL");
            var result = new ReviewResult { ReviewedFiles = 1, Requests = 1, RawDetections = 1 };
            result.Models.Add("model");
            result.Findings.Add(new Finding("sql-injection", Severity.High, "PRIVATE_MODEL_SENTINEL",
                "PRIVATE_MODEL_SENTINEL", files[0].Path, 1, null, 0.95,
                Review.Fingerprint("sql-injection", files[0].Path, files[0].NewLines[1]), "PRIVATE_FIX_SENTINEL"));
            using (var cache = new ReviewCache(path, new byte[32], "exact-input"))
            {
                cache.Write(files, result, "model", default);
                ReviewResult cached = Assert.IsType<ReviewResult>(cache.Read(files, new ReviewConfig(), null, "model", default));
                Assert.True(cached.CacheHit);
                Assert.Equal(0, cached.Requests);
                Assert.Equal(1, cached.CachedRequests);
                Assert.Equal(1, Review.ExitCode(cached.Findings, Severity.High));
                Finding finding = Assert.Single(cached.Findings);
                Assert.Equal(result.Findings[0].Fingerprint, finding.Fingerprint);
                Assert.Null(finding.SuggestedFix);
                Assert.Contains("Cached finding metadata", finding.Message);
            }
            using (var changed = new ReviewCache(path, new byte[32], "changed-input"))
                Assert.Null(changed.Read(files, new ReviewConfig(), null, "model", default));
            string bytes = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("PRIVATE_", bytes, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Rejected_findings_prevent_caching()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var cache = new ReviewCache(Path.Combine(directory, "cache.db"), new byte[32], "input");
            var result = new ReviewResult { ReviewedFiles = 0, Requests = 1 };
            result.Models.Add("model");
            result.Rejected.Add("invalid field");
            cache.Write([], result, "model", default);
            Assert.Null(cache.Read([], new ReviewConfig(), null, "model", default));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
