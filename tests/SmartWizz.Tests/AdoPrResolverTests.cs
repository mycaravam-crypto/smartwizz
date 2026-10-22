namespace SmartWizz.Tests;

public class AdoPrResolverTests
{
    const string Context = """
        {
          "pullRequest": {
            "pullRequestId": 142,
            "targetRefName": "refs/heads/main",
            "lastMergeSourceCommit": { "commitId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            "lastMergeTargetCommit": { "commitId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            "repository": { "id": "37ee1a7d-0f94-4a8a-a6fa-42478d85cd3b" }
          }
        }
        """;

    [Fact]
    public void Resolves_actual_pr_commits_and_repository()
    {
        var result = AdoPrResolver.ParseContext(Context, 142);
        Assert.Equal(new string('a', 40), result.Head);
        Assert.Equal(new string('b', 40), result.Base);
        Assert.Equal("refs/heads/main", result.TargetBranch);
    }

    [Fact]
    public void Rejects_metadata_for_different_pr() =>
        Assert.Throws<SmartWizzException>(() => AdoPrResolver.ParseContext(Context, 143));

    [Theory]
    [InlineData("--pr", "142", true)]
    [InlineData("--pr", "0", false)]
    public void Pr_option_requires_valid_id(string name, string value, bool valid)
    {
        if (valid) Assert.Equal(142, Options.Parse([name, value]).PrId);
        else Assert.Throws<SmartWizzException>(() => Options.Parse([name, value]));
    }

    [Fact]
    public void Pr_and_base_are_mutually_exclusive() =>
        Assert.Throws<SmartWizzException>(() => Options.Parse(["--pr", "142", "--base", "origin/main"]));
}
