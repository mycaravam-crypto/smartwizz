using System.Diagnostics;

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
    readonly bool _interactive;
    readonly object _gate = new();
    readonly Timer? _timer;
    ReviewProgress? _current;
    long _requestStarted;
    int _spinnerIndex;
    int _lastLength;
    bool _rendered;

    public ConsoleProgress(TextWriter writer, bool enabled, bool interactive = true)
    {
        _writer = writer;
        _enabled = enabled;
        _interactive = interactive;
        if (enabled && interactive)
            _timer = new Timer(Heartbeat, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Report(ReviewProgress value)
    {
        if (!_enabled) return;

        lock (_gate)
        {
            if (value.TotalRequests == 0)
            {
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
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
                    _timer?.Change(1000, 1000);
                }
                _current = value;
                Render(value, active: true);
                return;
            }

            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _current = value;
            Render(value, active: false);
        }
    }

    void Heartbeat(object? state)
    {
        lock (_gate)
        {
            if (_current is { RequestActive: true } current)
                Render(current, active: true);
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

        if (_interactive)
        {
            _writer.Write("\\r" + line.PadRight(_lastLength));
            _writer.Flush();
            _lastLength = line.Length;
            _rendered = true;
        }
        else
        {
            WriteLine(line);
        }
    }

    void WriteLine(string line)
    {
        _writer.Write("\\r" + line.PadRight(_lastLength));
        _writer.WriteLine();
        _writer.Flush();
        _lastLength = 0;
        _rendered = false;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate)
        {
            if (_rendered) _writer.WriteLine();
        }
    }
}
