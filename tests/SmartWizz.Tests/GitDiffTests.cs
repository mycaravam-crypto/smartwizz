using SmartWizz;

public class GitDiffTests
{
    public const string Sample = """
        diff --git a/src/Calc.cs b/src/Calc.cs
        index 1111111..2222222 100644
        --- a/src/Calc.cs
        +++ b/src/Calc.cs
        @@ -10,4 +10,5 @@ class Calc
             int Add(int a, int b)
             {
        -        return a - b;
        +        var r = a + b;
        +        return r;
             }
        diff --git a/old.txt b/old.txt
        deleted file mode 100644
        index 3333333..0000000
        --- a/old.txt
        +++ /dev/null
        @@ -1 +0,0 @@
        -gone
        diff --git a/logo.png b/logo.png
        index 4444444..5555555 100644
        Binary files a/logo.png and b/logo.png differ
        diff --git a/web/package-lock.json b/web/package-lock.json
        index 6666666..7777777 100644
        --- a/web/package-lock.json
        +++ b/web/package-lock.json
        @@ -1 +1 @@
        -{}
        +{"a":1}

        """;

    [Fact]
    public void Parses_files_and_new_side_line_numbers()
    {
        var files = GitDiff.Parse(Sample);

        Assert.Equal(["src/Calc.cs", "old.txt", "logo.png", "web/package-lock.json"], files.Select(f => f.Path));
        var calc = files[0];
        Assert.Equal([10, 11, 12, 13, 14], calc.NewLines.Keys.Order());
        Assert.Equal("        var r = a + b;", calc.NewLines[12]);
        Assert.Contains("    12 |+        var r = a + b;", calc.Annotated.ToString());
        Assert.Contains("       |-        return a - b;", calc.Annotated.ToString());
        Assert.True(files[1].IsDeleted);
        Assert.True(files[2].IsBinary);
    }

    [Fact]
    public void Added_line_starting_with_plus_plus_plus_is_content_not_header()
    {
        var files = GitDiff.Parse("diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1,0 +1,1 @@\n+++ b/evil\n");
        Assert.Equal("x", files.Single().Path);
        Assert.Equal("++ b/evil", files[0].NewLines[1]);
    }

    [Theory]
    [InlineData("web/package-lock.json", true)]
    [InlineData("app/site.min.js", true)]
    [InlineData("src/Data/Migrations/0001_Init.cs", true)]
    [InlineData("src/Calc.cs", false)]
    public void Excludes_by_path_or_file_name(string path, bool excluded) =>
        Assert.Equal(excluded, GitDiff.IsExcluded(path, new ReviewConfig().Exclude));
}
