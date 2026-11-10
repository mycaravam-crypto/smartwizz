namespace SmartWizz.Tests;

public class ConsoleProgressTests
{
    [Fact]
    public void Reports_completed_requests_and_files_without_touching_stdout()
    {
        var output = new StringWriter();
        using (var progress = new ConsoleProgress(output, enabled: true))
        {
            progress.Report(new ReviewProgress("Reviewing", 0, 4, 0, 8, RequestActive: true));
            progress.Report(new ReviewProgress("Reviewing", 1, 4, 4, 8));
        }

        string rendered = output.ToString();
        Assert.Contains("request 1/4", rendered);
        Assert.Contains("1/4 requests", rendered);
        Assert.Contains("4/8 files", rendered);
        Assert.EndsWith(Environment.NewLine, rendered);
    }

    [Fact]
    public void Disabled_progress_does_not_write()
    {
        var output = new StringWriter();
        using (var progress = new ConsoleProgress(output, enabled: false))
            progress.Report(new ReviewProgress("Reviewing", 1, 2, 1, 2));

        Assert.Equal("", output.ToString());
    }
}
