namespace SmartWizz;

public sealed record ReviewProgress(
    string Phase,
    int CompletedRequests,
    int TotalRequests,
    int ReviewedFiles,
    int TotalFiles,
    bool RequestActive = false);

/// <summary>Compact terminal progress written to stderr, leaving reports and machine-readable stdout untouched.</summary>
public sealed class ConsoleProgress(TextWriter writer, bool enabled) : IProgress<ReviewProgress>, IDisposable
{
    const int BarWidth = 20;
    bool _rendered;
    int _lastLength;

    public void Report(ReviewProgress value)
    {
        if (!enabled) return;

        if (value.TotalRequests == 0)
        {
            WriteLine("Reviewing: no reviewable files");
            return;
        }

        int completed = Math.Clamp(value.CompletedRequests, 0, value.TotalRequests);
        int filled = completed * BarWidth / value.TotalRequests;
        string bar = new string('#', filled) + new string('.', BarWidth - filled);
        string status = value.RequestActive
            ? $"request {completed + 1}/{value.TotalRequests}"
            : $"{completed}/{value.TotalRequests} requests";
        string line = $"{value.Phase} [{bar}] {status} | {value.ReviewedFiles}/{value.TotalFiles} files";
        writer.Write("\r" + line.PadRight(_lastLength));
        writer.Flush();
        _lastLength = line.Length;
        _rendered = true;
    }

    void WriteLine(string line)
    {
        writer.Write("\r" + line.PadRight(_lastLength));
        writer.WriteLine();
        writer.Flush();
        _lastLength = 0;
        _rendered = false;
    }

    public void Dispose()
    {
        if (_rendered) writer.WriteLine();
    }
}
