using System.Diagnostics;

namespace SmartWizz.Tests;

// Waits until cancelled, like a hung server.
public sealed class HangingHandler : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new UnreachableException();
    }
}

public class TimeoutTests
{
    static readonly AzureDevOpsConfig AzCfg = new()
    {
        CollectionUri = "https://tfs/",
        Project = "P",
        RepositoryId = "R",
        PullRequestId = 1,
        TimeoutSeconds = 1,
    };

    [Fact]
    public async Task Hung_provider_times_out()
    {
        var provider = new Provider(new HttpClient(new HangingHandler()), new ProviderConfig { Endpoint = "http://x", Model = "m", TimeoutSeconds = 1 }, null);
        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() => provider.CompleteAsync("s", "u", default));
        Assert.Equal(FailureKind.Timeout, e.Kind);
    }

    [Fact]
    public async Task Cancelled_provider_call_is_cancellation_not_timeout()
    {
        var provider = new Provider(new HttpClient(new HangingHandler()), new ProviderConfig { Endpoint = "http://x", Model = "m" }, null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync("s", "u", cts.Token));
    }

    [Fact]
    public async Task Hung_azure_devops_times_out_before_posting()
    {
        var handler = new HangingHandler();
        var finding = new Finding("r", Severity.Low, "t", "m", "a.cs", 1, null, 1, "fp");
        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() =>
            new AzureDevOpsPublisher(new HttpClient(handler), AzCfg, "tok").PublishAsync([finding], default));
        Assert.Equal(FailureKind.Timeout, e.Kind);
        Assert.Equal(1, handler.Calls); // only the thread lookup; nothing posted
    }

    [Fact]
    public async Task Failure_in_a_later_batch_fails_the_whole_review()
    {
        int calls = 0;
        var stub = new StubHandler((_, _) => ++calls == 1
            ? StubHandler.Chat("""{"findings":[]}""")
            : StubHandler.Json("busy", System.Net.HttpStatusCode.ServiceUnavailable));
        var provider = new Provider(new HttpClient(stub), new ProviderConfig { Endpoint = "http://x", Model = "m" }, null);
        List<FileDiff> files = GitDiff.Parse("""
            diff --git a/a.cs b/a.cs
            --- a/a.cs
            +++ b/a.cs
            @@ -1,0 +1,1 @@
            +a
            diff --git a/b.cs b/b.cs
            --- a/b.cs
            +++ b/b.cs
            @@ -1,0 +1,1 @@
            +b

            """);

        SmartWizzException e = await Assert.ThrowsAsync<SmartWizzException>(() =>
            Review.RunAsync(files, new ReviewConfig { MaxCharsPerRequest = 40 }, provider, "sys", default));
        Assert.Equal(FailureKind.Provider, e.Kind);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("""{ "provider": { "timeoutSeconds": 0 } }""")]
    [InlineData("""{ "azureDevOps": { "timeoutSeconds": -5 } }""")]
    [InlineData("""{ "review": { "contextLines": -1 } }""")]
    [InlineData("""{ "review": { "maxCharsPerRequest": 0 } }""")]
    [InlineData("""{ "review": { "minConfidence": 1.5 } }""")]
    public void Invalid_numeric_settings_fail_at_startup(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        try
        {
            SmartWizzException e = Assert.Throws<SmartWizzException>(() => Config.Load(path, _ => null));
            Assert.Equal(FailureKind.Configuration, e.Kind);
        }
        finally { File.Delete(path); }
    }
}

public class ExitCodeTests
{
    [Fact]
    public void Cancellation_wins_over_any_failure_kind()
    {
        Assert.Equal(Program.ExitCancelled, Program.Failure(new OperationCanceledException(), cancelled: true).Code);
        Assert.Equal(Program.ExitCancelled, Program.Failure(new SmartWizzException(FailureKind.Git, "killed"), cancelled: true).Code);
    }

    [Theory]
    [InlineData(FailureKind.Timeout, "error [timeout]: x")]
    [InlineData(FailureKind.InvalidModelOutput, "error [invalid-model-output]: x")]
    [InlineData(FailureKind.Provider, "error [provider]: x")]
    [InlineData(FailureKind.Publish, "error [publish]: x")]
    public void Failures_exit_2_with_their_kind(FailureKind kind, string message) =>
        Assert.Equal((2, message), Program.Failure(new SmartWizzException(kind, "x"), cancelled: false));

    [Fact]
    public void Unexpected_exceptions_exit_2() =>
        Assert.Equal((2, "error [unexpected]: boom"), Program.Failure(new InvalidOperationException("boom"), cancelled: false));
}

[Collection("process-env")] // mutates PATH
public class GitCancellationTests
{
    [Fact]
    public async Task Cancellation_kills_the_git_process()
    {
        if (!OperatingSystem.IsLinux()) return;

        string dir = Directory.CreateTempSubdirectory().FullName;
        string pidFile = Path.Combine(dir, "pid");
        string fakeGit = Path.Combine(dir, "git");
        File.WriteAllText(fakeGit, $"#!/bin/sh\necho $$ > '{pidFile}'\nexec sleep 30\n");
        File.SetUnixFileMode(fakeGit, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", $"{dir}:{oldPath}");
        try
        {
            using var cts = new CancellationTokenSource();
            Task<string> run = GitDiff.RunAsync(".", "base", "head", 3, cts.Token);
            int pid = await WaitForPid(pidFile);

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(await WaitUntil(() => !IsRunning(pid)), $"git (pid {pid}) still running after cancellation");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(dir, true);
        }
    }

    static async Task<int> WaitForPid(string pidFile)
    {
        int pid = 0;
        Assert.True(await WaitUntil(() => File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out pid)), "fake git never started");
        return pid;
    }

    // Zombie (state Z) counts as stopped: the process has exited and only awaits reaping.
    static bool IsRunning(int pid)
    {
        string stat = $"/proc/{pid}/stat";
        try
        {
            return File.Exists(stat) && File.ReadAllText(stat).Split(' ')[2] != "Z";
        }
        catch (IOException)
        {
            return false;
        }
    }

    static async Task<bool> WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
