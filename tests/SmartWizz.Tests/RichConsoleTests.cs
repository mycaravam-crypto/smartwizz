using Spectre.Console;

namespace SmartWizz.Tests;

public class RichConsoleTests
{
    [Fact]
    public void Rich_report_preserves_finding_details_without_interpreting_markup()
    {
        using var output = new StringWriter();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
        });
        console.Profile.Width = 180;
        var result = new ReviewResult { ReviewedFiles = 1, Requests = 1 };
        result.Findings.Add(new Finding("test-rule", Severity.High, "Unsafe [red] text",
            "Review message", "src/Test.cs", 59, null, 0.95, "abc123", "Use safe configuration"));
        result.ReviewElapsedMilliseconds = 1500;
        result.RequestTimings.Add(new ReviewRequestTiming(1, "m", 1250, 1, 100, 200, 50, 10, 1, false));
        result.FindingRequests.Add("abc123", 1);
        Report.WriteRichConsole(result, console);
        string rendered = output.ToString();
        Assert.Contains("HIGH", rendered);
        Assert.Contains("Unsafe [red] text", rendered);
        Assert.Contains("src/Test.cs:59", rendered);
        Assert.Contains("Review message", rendered);
        Assert.Contains("Use safe configuration", rendered);
        Assert.Contains("abc123", rendered);
        Assert.Contains("Request #1: 1.25s (shared call)", rendered);
        Assert.Contains("LLM calls: 1.25s", rendered);
        Assert.DoesNotContain("SmartWizz: 1 finding", rendered);
        Assert.All(rendered.Split('\n'), line => Assert.True(line.TrimEnd().Length <= 110));
    }

    [Fact]
    public void Verbose_is_opt_in()
    {
        Assert.False(Options.Parse(["check"]).Verbose);
        Assert.True(Options.Parse(["check", "--verbose"]).Verbose);
    }
}
