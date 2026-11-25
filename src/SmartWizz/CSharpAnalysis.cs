using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SmartWizz;

internal static class CSharpAnalysis
{
    // Parse contiguous visible diff fragments only. Never load projects, execute builds,
    // or imply semantic type resolution from a partial source snapshot.
    internal static List<Finding> Analyze(FileDiff file, CancellationToken ct)
    {
        var findings = new List<Finding>();
        if (!file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            file.IsBinary || file.IsDeleted || file.NewLines.Count == 0) return findings;
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
        return findings;

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
                if (!file.AddedLines.Contains(line) || !file.NewLines.TryGetValue(line, out string? source)) continue;
                findings.Add(new Finding("off-by-one", Severity.Medium, "Potential inclusive array bounds error",
                    "The loop includes Length while directly indexing the same receiver. For arrays this permits an out-of-range access; verify the receiver type and use an exclusive upper bound.",
                    file.Path, line, null, 0.95, Review.Fingerprint("off-by-one", file.Path, source.Trim())));
            }
        }
    }
}
