using System.Text.RegularExpressions;

namespace SmartWizz;

// The comment SmartWizz posts for a finding, shared by every publisher so reruns recognise their own comments.
public static partial class PrComment
{
    // Visible on purpose: HTML comments are not reliably preserved by every server.
    public static string Marker(Finding f) => $"SmartWizz fingerprint: {f.Fingerprint}";

    public static string Text(Finding f)
    {
        string lines = f.EndLine is { } end ? $" · lines {f.StartLine}-{end}" : "";
        return $"**[{f.Severity}] {f.Title}**\n\n{f.Message}\n\n" +
               $"_`{f.RuleId}` · confidence {f.Confidence:0.00}{lines} · AI-generated review comment, verify before acting._\n\n" +
               $"<sub>{Marker(f)}</sub>";
    }

    [GeneratedRegex(@"SmartWizz fingerprint: ([0-9a-f]{16})")]
    private static partial Regex FingerprintMarker();

    public static IEnumerable<string> Fingerprints(string commentText) =>
        FingerprintMarker().Matches(commentText).Select(m => m.Groups[1].Value);
}
