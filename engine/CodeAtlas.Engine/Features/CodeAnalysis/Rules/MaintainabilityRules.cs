using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class MemberMetrics
{
    public static IEnumerable<SyntaxNode> Members(RuleContext context) =>
        context.Root.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax { Body: not null } or BaseMethodDeclarationSyntax { ExpressionBody: not null }
            or AccessorDeclarationSyntax { Body: not null });

    public static string Describe(RuleContext context, SyntaxNode member) =>
        context.Model.GetDeclaredSymbol(member, context.Cancellation)
            ?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? "(member)";

    public static SyntaxToken NameToken(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier,
        ConstructorDeclarationSyntax c => c.Identifier,
        DestructorDeclarationSyntax d => d.Identifier,
        AccessorDeclarationSyntax a => a.Keyword,
        _ => member.GetFirstToken()
    };
}

/// <summary>McCabe cyclomatic complexity per member: 1 + the number of independent decision points.</summary>
sealed class CyclomaticComplexityRule : ICodeRule
{
    private const int DefaultThreshold = 15;

    public RuleDescriptor Descriptor { get; } = new("ATL1009", "CyclomaticComplexity", "maintainability",
        FindingSeverity.Info, $"Member whose cyclomatic complexity exceeds the threshold (default {DefaultThreshold}).");

    public void Analyze(RuleContext context)
    {
        var threshold = context.Threshold ?? DefaultThreshold;
        foreach (var member in MemberMetrics.Members(context))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            void Add(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

            foreach (var node in member.DescendantNodes())
            {
                switch (node)
                {
                    case IfStatementSyntax: Add("if"); break;
                    case ConditionalExpressionSyntax: Add("?:"); break;
                    case WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or ForEachStatementSyntax: Add("loop"); break;
                    case CatchClauseSyntax: Add("catch"); break;
                    case CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax: Add("case"); break;
                    case SwitchExpressionArmSyntax arm when arm.Pattern is not DiscardPatternSyntax: Add("switch-arm"); break;
                    case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression): Add("&&/||"); break;
                    case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression): Add("??"); break;
                    case AssignmentExpressionSyntax a when a.IsKind(SyntaxKind.CoalesceAssignmentExpression): Add("??="); break;
                    case BinaryPatternSyntax p when p.IsKind(SyntaxKind.AndPattern) || p.IsKind(SyntaxKind.OrPattern): Add("and/or pattern"); break;
                }
            }

            var complexity = 1 + counts.Values.Sum();
            if (complexity <= threshold) continue;

            var name = MemberMetrics.Describe(context, member);
            context.Report(MemberMetrics.NameToken(member).Parent ?? member,
                $"{name} has cyclomatic complexity {complexity} (threshold {threshold})",
                $"The member has {complexity} independent paths: {string.Join(", ", counts.Select(kv => $"{kv.Value}× {kv.Key}"))}.",
                ValidationLevel.StaticProven, "high",
                new[] { new FindingEvidence("metric", $"cyclomatic complexity = 1 + {string.Join(" + ", counts.Select(kv => $"{kv.Value} ({kv.Key})"))} = {complexity}", context.LocationOf(MemberMetrics.NameToken(member))) },
                "More paths mean more test cases are needed to cover the member and more places where a change can alter behavior. This is a maintenance indicator, not a defect.",
                "Extract cohesive branches into named methods, replace condition chains with polymorphism or tables where that matches the domain, and cover each extracted unit with tests before refactoring.",
                "Measure branch coverage of the member with a coverage tool; a complexity of N needs at least N paths exercised for full branch coverage.",
                new[] { "The metric is exact for the counting rules above; whether the member should be split is a judgement.", "Switch statements with many trivial cases inflate the metric without being hard to read." },
                complexity >= threshold * 2 ? FindingSeverity.Warning : null,
                discriminator: name);
        }
    }
}

/// <summary>Members spanning many non-blank, non-comment lines.</summary>
sealed class MethodLengthRule : ICodeRule
{
    private const int DefaultThreshold = 80;

    public RuleDescriptor Descriptor { get; } = new("ATL1010", "MethodLength", "maintainability",
        FindingSeverity.Info, $"Member with more than {DefaultThreshold} code lines (configurable).");

    public void Analyze(RuleContext context)
    {
        var threshold = context.Threshold ?? DefaultThreshold;
        var text = context.Tree.GetText(context.Cancellation);
        foreach (var member in MemberMetrics.Members(context))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var span = context.Tree.GetLineSpan(member.Span);
            var codeLines = 0;
            for (var line = span.StartLinePosition.Line; line <= span.EndLinePosition.Line; line++)
            {
                var content = text.Lines[line].ToString().Trim();
                if (content.Length == 0 || content.StartsWith("//", StringComparison.Ordinal) || content is "{" or "}") continue;
                codeLines++;
            }
            if (codeLines <= threshold) continue;

            var name = MemberMetrics.Describe(context, member);
            context.Report(MemberMetrics.NameToken(member).Parent ?? member,
                $"{name} spans {codeLines} code lines (threshold {threshold})",
                "The member is long. Length alone does not show mixed responsibilities; it marks where to look for them.",
                ValidationLevel.StaticProven, "high",
                new[] { new FindingEvidence("metric", $"{codeLines} non-blank, non-comment, non-brace lines between lines {span.StartLinePosition.Line + 1} and {span.EndLinePosition.Line + 1}.", context.LocationOf(MemberMetrics.NameToken(member))) },
                "Longer members are harder to review and test in isolation. This is a maintenance indicator, not a defect.",
                "Look for sections with their own inputs and outputs (validation, mapping, persistence, side effects) and extract them. Hypothesis to confirm with the team: these sections correspond to separate responsibilities.",
                "Not applicable: this is a static metric. Verify improvements by comparing the metric and the test coverage before and after the change.",
                new[] { "Generated-looking tables or long switch statements may be legitimately long." },
                discriminator: name);
        }
    }
}

/// <summary>Syntax errors do not depend on references, so they are reported as proven even without restored packages.</summary>
sealed class SyntaxErrorRule : ICodeRule
{
    public RuleDescriptor Descriptor { get; } = new("ATL0001", "SyntaxError", "compilation",
        FindingSeverity.Error, "The file contains a syntax error.");

    public void Analyze(RuleContext context)
    {
        foreach (var diagnostic in context.Tree.GetDiagnostics(context.Cancellation).Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            var node = context.Root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            context.Report(node, $"{diagnostic.Id}: {diagnostic.GetMessage()}", diagnostic.GetMessage(),
                ValidationLevel.StaticProven, "high",
                new[] { new FindingEvidence("compiler-diagnostic", $"{diagnostic.Id} reported by the Roslyn parser.", context.LocationOf(node), context.Snippet(node)) },
                "The file cannot be compiled; semantic rules may be incomplete for this file.",
                "Fix the syntax error reported by the compiler.",
                "Build the project; the compiler reports the same diagnostic.",
                new[] { "Errors about missing types or packages (CS0246 and similar) are not reported here because analysis runs without restoring packages." },
                discriminator: diagnostic.Id + diagnostic.GetMessage());
        }
    }
}
