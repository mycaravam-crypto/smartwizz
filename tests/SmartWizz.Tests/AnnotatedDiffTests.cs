namespace SmartWizz.Tests;

public class AnnotatedDiffTests
{
    [Fact]
    public void Anchors_comment_to_added_line_not_removed_or_context_lines()
    {
        string diff = "diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -1,2 +1,2 @@\n-old\n+new\n keep\n";
        var finding = new Finding("SW-1", Severity.High, "Problem", "Explain", "a.cs", 1, null, 0.9, "fp");
        string annotated = AnnotatedDiff.Render(diff, [finding]);
        Assert.Contains("+new\n# REVIEW [HIGH] Problem", annotated);
        Assert.Contains("#   Explain", annotated);
        Assert.Equal(1, annotated.Split("# REVIEW").Length - 1);
    }

    [Fact]
    public void Does_not_insert_comments_in_unrelated_files()
    {
        string diff = "diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -0,0 +1 @@\n+one\ndiff --git a/b.cs b/b.cs\n--- a/b.cs\n+++ b/b.cs\n@@ -0,0 +1 @@\n+two\n";
        var finding = new Finding("SW-2", Severity.Medium, "B only", "Detail", "b.cs", 1, null, 0.8, "fp");
        string result = AnnotatedDiff.Render(diff, [finding]);
        Assert.DoesNotContain("+one\n# REVIEW", result);
        Assert.Contains("+two\n# REVIEW [MEDIUM] B only", result);
    }
}
