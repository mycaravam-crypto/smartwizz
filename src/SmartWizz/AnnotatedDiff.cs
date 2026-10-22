using System.Text;
using System.Text.RegularExpressions;

namespace SmartWizz;

// Report-only review artifact. Comments are anchored to added lines of the new side;
// no Azure DevOps API calls are made by this renderer.
public static class AnnotatedDiff
{
    static readonly Regex Hunk = new(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

    public static string Render(string diff, IReadOnlyList<Finding> findings)
    {
        var output = new StringBuilder();
        var grouped = findings.GroupBy(f => (f.FilePath, f.StartLine))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.Severity).ToList());
        string? path = null;
        int newLine = 0;
        bool inHunk = false;
        foreach (string line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                path = null;
                inHunk = false;
            }
            else if (line.StartsWith("+++ b/", StringComparison.Ordinal) && !inHunk)
                path = line[6..];
            Match match = Hunk.Match(line);
            if (match.Success)
            {
                newLine = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                inHunk = true;
            }
            output.Append(line).Append('\n');
            if (!inHunk || match.Success || line.Length == 0) continue;
            if (line[0] is '+' or ' ')
            {
                if (line[0] == '+' && path is not null && grouped.TryGetValue((path, newLine), out List<Finding>? matched))
                    foreach (Finding finding in matched)
                    {
                        output.Append("# REVIEW [").Append(finding.Severity.ToString().ToUpperInvariant())
                            .Append("] ").Append(finding.Title).Append(" (")
                            .Append(finding.RuleId).Append(", confidence ")
                            .Append(finding.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                            .AppendLine(")");
                        foreach (string messageLine in finding.Message.Split('\n'))
                            output.Append("#   ").AppendLine(messageLine);
                    }
                newLine++;
            }
            else if (line[0] is not ('-' or '\\'))
                inHunk = false;
        }
        return output.ToString();
    }
}
