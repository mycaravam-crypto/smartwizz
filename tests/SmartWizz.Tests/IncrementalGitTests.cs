using System.Diagnostics;

namespace SmartWizz.Tests;

public class IncrementalGitTests
{
    static void Git(string path, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(path);
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using Process p = Process.Start(psi)!;
        string error = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, error);
    }

    static string Sha(string path)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("HEAD");
        using Process p = Process.Start(psi)!;
        string sha = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return sha;
    }

    [Fact]
    public async Task Incremental_review_only_includes_new_fix_commit_changes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "smartwizz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Git(dir, "init", "-b", "main");
            Git(dir, "config", "user.email", "test@example.invalid");
            Git(dir, "config", "user.name", "SmartWizz Test");
            File.WriteAllText(Path.Combine(dir, "a.cs"), "original\n");
            Git(dir, "add", ".");
            Git(dir, "commit", "-m", "base");
            Git(dir, "checkout", "-b", "feature");
            File.WriteAllText(Path.Combine(dir, "a.cs"), "changed\n");
            Git(dir, "add", ".");
            Git(dir, "commit", "-m", "initial PR");
            string reviewed = Sha(dir);
            File.WriteAllText(Path.Combine(dir, "b.cs"), "fix changed this\n");
            Git(dir, "add", ".");
            Git(dir, "commit", "-m", "follow-up fix");
            string head = Sha(dir);

            string full = await GitDiff.RunIncrementalAsync(dir, "main", head, null, 3, default);
            string delta = await GitDiff.RunIncrementalAsync(dir, "main", head, reviewed, 3, default);
            Assert.Contains("a.cs", full);
            Assert.Contains("b.cs", full);
            Assert.DoesNotContain("a.cs", delta);
            Assert.Contains("b.cs", delta);

            // Baseline SHA is a valid commit but not an ancestor of a rewritten branch.
            Git(dir, "checkout", "main");
            File.WriteAllText(Path.Combine(dir, "rewritten.cs"), "new history\n");
            Git(dir, "add", ".");
            Git(dir, "commit", "-m", "rewrite");
            string rewritten = Sha(dir);
            Assert.False(await GitDiff.IsAncestorAsync(dir, reviewed, rewritten, default));
            string fallback = await GitDiff.RunIncrementalAsync(dir, "main", rewritten, reviewed, 3, default);
            Assert.Equal(await GitDiff.RunAsync(dir, "main", rewritten, 3, default), fallback);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
