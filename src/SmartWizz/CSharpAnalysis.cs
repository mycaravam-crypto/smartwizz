using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SmartWizz;

internal static class CSharpAnalysis
{
    // Parse contiguous visible diff fragments only. Never load projects, execute builds,
    // or imply semantic type resolution from a partial source snapshot.
    internal static Dictionary<int, int> AttributionMap(FileDiff file, CancellationToken ct)
    {
        var candidates = new Dictionary<int, HashSet<int>>();
        if (!file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            file.IsBinary || file.IsDeleted || file.NewLines.Count == 0) return [];
        var lines = new List<string>();
        int first = 0, previous = 0;
        foreach ((int number, string text) in file.NewLines.OrderBy(entry => entry.Key))
        {
            ct.ThrowIfCancellationRequested();
            if (lines.Count > 0 && number != previous + 1)
            {
                Scan();
                lines.Clear();
            }
            if (lines.Count == 0) first = number;
            lines.Add(text);
            previous = number;
        }
        Scan();
        return candidates.Where(entry => entry.Value.Count == 1)
            .ToDictionary(entry => entry.Key, entry => entry.Value.Single());

        void Scan()
        {
            if (lines.Count == 0 || lines.Sum(line => (long)line.Length + 1) > 60_000) return;
            SyntaxTree tree = CSharpSyntaxTree.ParseText(string.Join("\n", lines), cancellationToken: ct);
            foreach (ForStatementSyntax loop in tree.GetRoot(ct).DescendantNodes().OfType<ForStatementSyntax>())
            {
                ct.ThrowIfCancellationRequested();
                if (loop.ContainsDiagnostics || loop.Declaration is not { Variables.Count: 1 } declaration ||
                    declaration.Variables[0] is not { Initializer.Value: LiteralExpressionSyntax zero } variable ||
                    !zero.IsKind(SyntaxKind.NumericLiteralExpression) || zero.Token.Value is not int value || value != 0 ||
                    loop.Condition is not BinaryExpressionSyntax condition || !condition.IsKind(SyntaxKind.LessThanOrEqualExpression) ||
                    condition.Left is not IdentifierNameSyntax index || index.Identifier.ValueText != variable.Identifier.ValueText ||
                    condition.Right is not MemberAccessExpressionSyntax length || length.Name.Identifier.ValueText != "Length" ||
                    length.Expression is not IdentifierNameSyntax array || loop.Incrementors.Count != 1 ||
                    loop.Incrementors[0] is not PostfixUnaryExpressionSyntax increment || !increment.IsKind(SyntaxKind.PostIncrementExpression) ||
                    increment.Operand is not IdentifierNameSyntax incrementIndex || incrementIndex.Identifier.ValueText != index.Identifier.ValueText)
                    continue;
                // Only straight-line bodies with a direct indexed access; skip control flow
                // and assignments to the index or array. This is lexical evidence, not proof.
                StatementSyntax[] statements = loop.Statement is BlockSyntax block ? [.. block.Statements] : [loop.Statement];
                if (statements.Length != 1 || statements[0] is not ExpressionStatementSyntax expression ||
                    expression.Expression is not AssignmentExpressionSyntax assignment ||
                    assignment.Left is not IdentifierNameSyntax target ||
                    target.Identifier.ValueText == index.Identifier.ValueText || target.Identifier.ValueText == array.Identifier.ValueText ||
                    assignment.Right is not ElementAccessExpressionSyntax access ||
                    access.Expression is not IdentifierNameSyntax receiver || receiver.Identifier.ValueText != array.Identifier.ValueText ||
                    access.ArgumentList.Arguments.Count != 1 ||
                    access.ArgumentList.Arguments[0].Expression is not IdentifierNameSyntax subscript ||
                    subscript.Identifier.ValueText != index.Identifier.ValueText)
                    continue;
                int line = first + tree.GetLineSpan(condition.Span, ct).StartLinePosition.Line;
                if (!file.AddedLines.Contains(line)) continue;
                FileLinePositionSpan span = tree.GetLineSpan(loop.Span, ct);
                int start = first + span.StartLinePosition.Line;
                int end = first + span.EndLinePosition.Line;
                // Do not infer a location outside the actual visible syntax node.
                for (int reported = start; reported <= end; reported++)
                {
                    if (!file.AddedLines.Contains(reported)) continue;
                    if (!candidates.TryGetValue(reported, out HashSet<int>? targets))
                        candidates.Add(reported, targets = []);
                    targets.Add(line);
                }
            }
        }
    }

    internal static Finding Attribute(Finding finding, FileDiff file, IReadOnlyDictionary<int, int> map)
    {
        // Only the narrow existing bounds category is supported. This never creates a finding.
        if (RuleTaxonomy.Canonical(finding.RuleId) is not ("off-by-one" or "index-out-of-range" or "array-out-of-bounds") ||
            !map.TryGetValue(finding.StartLine, out int line) || line == finding.StartLine ||
            !file.AddedLines.Contains(line) || !file.NewLines.TryGetValue(line, out string? source))
            return finding;
        return finding with
        {
            StartLine = line,
            EndLine = null,
            SuggestedFix = null,
            Fingerprint = Review.Fingerprint(finding.RuleId, file.Path, source),
        };
    }
}
