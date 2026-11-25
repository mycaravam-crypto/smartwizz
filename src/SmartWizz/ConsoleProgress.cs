using System.Diagnostics;
using Spectre.Console;

namespace SmartWizz;

public sealed record ReviewProgress(
    string Phase,
    int CompletedRequests,
    int TotalRequests,
    int ReviewedFiles,
    int TotalFiles,
    bool RequestActive = false);

/// <summary>Compact terminal progress written to stderr, leaving reports and machine-readable stdout untouched.</summary>
public sealed class ConsoleProgress : IProgress<ReviewProgress>, IDisposable
{
    const int BarWidth = 20;
    static readonly char[] Spinner = ['|', '/', '-', '\\'];

    readonly TextWriter _writer;
    readonly bool _enabled;
    readonly object _gate = new();
    readonly IAnsiConsole? _console;
    ReviewProgress? _current;
    long _requestStarted;
    int _spinnerIndex;
    bool _disposed;

    public ConsoleProgress(TextWriter writer, bool enabled, bool interactive = true, IAnsiConsole? console = null)
    {
        _writer = writer;
        _enabled = enabled;
        _console = interactive && console is not null && console.Profile.Capabilities.Interactive ? console : null;
    }

    public void Report(ReviewProgress value)
    {
        if (!_enabled) return;

        lock (_gate)
        {
            if (_disposed) return;
            if (value.TotalRequests == 0)
            {

                WriteLine("Reviewing: no reviewable files");
                _current = null;
                return;
            }

            if (value.RequestActive)
            {
                if (_current?.RequestActive != true)
                {
                    _requestStarted = Stopwatch.GetTimestamp();
                    _spinnerIndex = 0;

                }
                _current = value;
                Render(value, active: true);
                return;
            }

            _current = value;
            Render(value, active: false);
        }
    }

    void Render(ReviewProgress value, bool active)
    {
        int completed = Math.Clamp(value.CompletedRequests, 0, value.TotalRequests);
        int filled = completed * BarWidth / value.TotalRequests;
        string bar = new string('#', filled) + new string('.', BarWidth - filled);
        string status = active
            ? $"{Spinner[_spinnerIndex++ % Spinner.Length]} request {completed + 1}/{value.TotalRequests} | {Stopwatch.GetElapsedTime(_requestStarted):m\\:ss}"
            : $"{completed}/{value.TotalRequests} requests";
        string line = $"{value.Phase} [{bar}] {status} | {value.ReviewedFiles}/{value.TotalFiles} files";

        // Use complete lines, even in rich mode: provider retries also write to stderr.
        // Never leave the cursor on an unfinished line or overwrite diagnostics.
        if (_console is not null)
        {
            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddRow(
                new Markup("[bold cyan]" + Markup.Escape(value.Phase) + "[/]"),
                new Markup("[green]" + new string('=', filled) + "[/][grey]" + new string('-', BarWidth - filled) + "[/]"),
                new Text($"{status} | {value.ReviewedFiles}/{value.TotalFiles} files"));
            _console.Write(grid);
        }
        else
        {
            WriteLine(line);
        }
    }

    void WriteLine(string line)
    {
        _writer.WriteLine(line);
        _writer.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _current = null;
        }
    }
}
