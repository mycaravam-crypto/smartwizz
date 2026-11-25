namespace SmartWizz.Tests;

public class StorageOptionsTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public void Local_defaults_require_prior_initialization(bool initialized, bool remember, bool cache)
    {
        Options options = Options.Parse(["check"]).ResolveStorage(new StorageConfig(), false, true, initialized);
        Assert.Equal(remember, options.Remember);
        Assert.Equal(cache, options.Cache);
    }

    [Fact]
    public void Trusted_config_can_initialize_but_cli_overrides_it()
    {
        var config = new StorageConfig
        {
            Remember = true,
            Cache = true,
        };
        Options normal = Options.Parse(["check"]).ResolveStorage(config, false, true, false);
        Assert.True(normal.Remember);
        Assert.True(normal.Cache);
        Assert.True(normal.InitializeHistory);
        Options fresh = Options.Parse(["check", "--no-cache"]).ResolveStorage(config, false, true, false);
        Assert.True(fresh.Remember);
        Assert.False(fresh.Cache);
        Options stateless = Options.Parse(["check", "--stateless", "--cache"]).ResolveStorage(config, false, true, true);
        Assert.False(stateless.Remember);
        Assert.False(stateless.Cache);
        Assert.False(stateless.InitializeHistory);
    }

    [Fact]
    public void Ci_and_pr_review_do_not_use_local_storage()
    {
        var config = new StorageConfig
        {
            Remember = true,
            Cache = true,
        };
        Options ci = Options.Parse(["check", "--remember", "--cache"]).ResolveStorage(config, true, true, true);
        Assert.False(ci.Remember);
        Assert.False(ci.Cache);
        Options pr = Options.Parse(["--base", "main"]).ResolveStorage(config, false, true, true);
        Assert.False(pr.Remember);
        Assert.False(pr.Cache);
    }

    [Fact]
    public void Quality_gate_and_annotated_diff_default_to_fresh_review()
    {
        Options gate = Options.Parse(["check", "--fail-on", "high"]).ResolveStorage(new StorageConfig(), false, true, true);
        Assert.True(gate.Remember);
        Assert.False(gate.Cache);
        Options annotated = Options.Parse(["check", "--annotated-diff", "report.txt"]).ResolveStorage(new StorageConfig(), false, true, true);
        Assert.False(annotated.Cache);
    }

    [Fact]
    public void Untrusted_repository_config_cannot_initialize_storage()
    {
        Options options = Options.Parse(["--staged"]).ResolveStorage(new StorageConfig { Remember = true }, false, false, false);
        Assert.False(options.Remember);
        Assert.False(options.InitializeHistory);
    }
}
