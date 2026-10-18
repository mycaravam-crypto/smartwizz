

namespace SmartWizz.Tests;

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
        Assert.Equal([12, 13], calc.AddedLines.Order());
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

    [Fact]
    public void New_renamed_and_mode_only_files_get_correct_paths_and_lines()
    {
        const string diff = """
            diff --git a/new.cs b/new.cs
            new file mode 100644
            index 0000000..1111111
            --- /dev/null
            +++ b/new.cs
            @@ -0,0 +1,2 @@
            +line one
            +line two
            diff --git a/old name.cs b/new name.cs
            similarity index 90%
            rename from old name.cs
            rename to new name.cs
            index 2222222..3333333 100644
            --- a/old name.cs
            +++ b/new name.cs
            @@ -5,2 +5,2 @@
             keep
            -was
            +now
            diff --git a/moved.cs b/dir/moved.cs
            similarity index 100%
            rename from moved.cs
            rename to dir/moved.cs
            diff --git a/run.sh b/run.sh
            old mode 100644
            new mode 100755

            """;
        var files = GitDiff.Parse(diff);

        Assert.Equal(["new.cs", "new name.cs", "dir/moved.cs", "run.sh"], files.Select(f => f.Path));
        Assert.Equal([1, 2], files[0].AddedLines.Order());
        Assert.Equal([6], files[1].AddedLines);
        Assert.Equal([5, 6], files[1].NewLines.Keys.Order());
        Assert.Empty(files[2].NewLines); // pure rename: nothing to review
        Assert.Empty(files[3].NewLines); // mode change only
        Assert.All(files, f => Assert.False(f.IsDeleted || f.IsBinary));
    }

    [Theory]
    [InlineData("web/package-lock.json", true)]
    [InlineData("app/site.min.js", true)]
    [InlineData("src/Data/Migrations/0001_Init.cs", true)]
    [InlineData("src/Calc.cs", false)]
    public void Excludes_by_path_or_file_name(string path, bool excluded) =>
        Assert.Equal(excluded, GitDiff.IsExcluded(path, new ReviewConfig().Exclude));
}
