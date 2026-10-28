using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>A deferred sequence (IEnumerable/IQueryable) that is enumerated more than once, or once per loop iteration.</summary>
sealed class MultipleEnumerationRule : ICodeRule
{
    // Operators that only wrap the source; they do not enumerate it themselves.
    private static readonly HashSet<string> Deferred = new(StringComparer.Ordinal)
    {
        "Where", "Select", "SelectMany", "Skip", "Take", "SkipWhile", "TakeWhile", "OrderBy", "OrderByDescending", "ThenBy",
        "ThenByDescending", "Reverse", "Concat", "Union", "Intersect", "Except", "Distinct", "DistinctBy", "Zip", "Cast", "OfType",
        "GroupBy", "Join", "GroupJoin", "AsEnumerable", "DefaultIfEmpty", "Append", "Prepend", "Chunk", "SkipLast", "TakeLast",
        "UnionBy", "IntersectBy", "ExceptBy", "AsQueryable", "Include", "ThenInclude", "AsNoTracking"
    };

    public RuleDescriptor Descriptor { get; } = new("ATL1008", "MultipleEnumeration", "performance",
        FindingSeverity.Warning, "A deferred sequence is enumerated more than once.");

    public void Analyze(RuleContext context)
    {
        foreach (var member in context.Root.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            SyntaxNode? body = member switch
            {
                BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                _ => null
            };
            if (body is null) continue;

            foreach (var candidate in Candidates(context, member, body))
                Evaluate(context, body, candidate.Symbol, candidate.IsQueryable);
        }
    }

    private static IEnumerable<(ISymbol Symbol, bool IsQueryable)> Candidates(RuleContext context, SyntaxNode member, SyntaxNode body)
    {
        var parameters = member is BaseMethodDeclarationSyntax m ? m.ParameterList.Parameters : default;
        foreach (var parameter in parameters)
            if (context.Model.GetDeclaredSymbol(parameter, context.Cancellation) is { } symbol && SequenceKind(symbol.Type) is { } kind)
                yield return (symbol, kind);

        foreach (var declarator in body.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer?.Value is not InvocationExpressionSyntax invocation) continue;
            if (context.Model.GetSymbolInfo(invocation, context.Cancellation).Symbol is not IMethodSymbol method) continue;
            if (!IsLinq(method) || !Deferred.Contains(method.Name)) continue;
            if (context.Model.GetDeclaredSymbol(declarator, context.Cancellation) is ILocalSymbol local && SequenceKind(local.Type) is { } kind)
                yield return (local, kind);
        }
    }

    /// <returns>false for IEnumerable&lt;T&gt;, true for IQueryable&lt;T&gt;, null when the type is not a deferred sequence.</returns>
    private static bool? SequenceKind(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true } named) return null;
        var definition = named.OriginalDefinition;
        if (definition.IsNamed("System.Collections.Generic", "IEnumerable")) return false;
        if (definition.IsNamed("System.Linq", "IQueryable")) return true;
        return null;
    }

    private static bool IsLinq(IMethodSymbol method) =>
        method.ContainingType.ContainingNamespace?.ToDisplayString() == "System.Linq" || method.ContainingType.ContainingNamespace?.ToDisplayString().StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true;

    private void Evaluate(RuleContext context, SyntaxNode body, ISymbol symbol, bool queryable)
    {
        if (IsReassigned(context, body, symbol)) return; // e.g. items = items.ToList(): materialized, single source of truth.

        var sites = new List<SyntaxNode>();
        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case ForEachStatementSyntax forEach when Refers(context, forEach.Expression, symbol):
                    sites.Add(forEach.Expression);
                    break;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } invocation
                    when Refers(context, access.Expression, symbol)
                         && context.Model.GetSymbolInfo(invocation, context.Cancellation).Symbol is IMethodSymbol method
                         && method.ReducedFrom is not null && IsLinq(method) && !Deferred.Contains(method.Name):
                    sites.Add(invocation);
                    break;
            }
        }
        if (sites.Count == 0) return;

        var perIteration = sites.FirstOrDefault(site => IsInLoopBody(site, body, symbol, context));
        var conflicting = sites.Count >= 2 ? FirstNonExclusivePair(sites) : null;
        if (conflicting is null && perIteration is null) return;

        var anchor = perIteration ?? conflicting!.Value.Second;
        var what = queryable ? "IQueryable (each enumeration may execute the query again)" : "IEnumerable (each enumeration re-runs the deferred pipeline)";
        var evidence = sites.Select(site => context.Evidence("enumeration", $"'{symbol.Name}' is enumerated here.", site)).ToList();
        if (perIteration is not null)
            evidence.Add(new FindingEvidence("loop", $"The enumeration at line {context.Tree.GetLineSpan(perIteration.Span).StartLinePosition.Line + 1} is inside a loop but '{symbol.Name}' is declared outside it."));

        context.Report(anchor,
            perIteration is not null && conflicting is null
                ? $"'{symbol.Name}' is enumerated on every loop iteration"
                : $"'{symbol.Name}' is enumerated {sites.Count} times",
            $"'{symbol.Name}' is typed as {what}; the enumeration sites listed in the evidence each walk the sequence.",
            ValidationLevel.StaticRisk, "medium", evidence.ToArray(),
            queryable
                ? "For a database-backed IQueryable each enumeration can be a separate query; for an in-memory pipeline the upstream work and side effects repeat. Not measured."
                : "Upstream computation (and any side effects or I/O in the pipeline) repeats per enumeration, and results may differ between passes. Not measured.",
            $"Materialize once ('var list = {symbol.Name}.ToList();') and reuse it, or accept IReadOnlyCollection<T>/IList<T> when callers already hold a collection.",
            "Add a test that passes a sequence with an invocation counter in its pipeline (yield-based generator) and assert the counter equals the element count, not a multiple of it.",
            new[]
            {
                "The runtime value may already be a materialized collection; only the declared type is analyzed.",
                "Enumeration sites in mutually exclusive branches are excluded; other control-flow exclusions (early returns) are not modelled.",
                "Enumerations performed inside callees that receive the sequence as an argument are not counted."
            },
            discriminator: $"{symbol.Name}:{symbol.Kind}");
    }

    private static bool Refers(RuleContext context, ExpressionSyntax expression, ISymbol symbol) =>
        expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == symbol.Name
        && SymbolEqualityComparer.Default.Equals(context.Model.GetSymbolInfo(identifier, context.Cancellation).Symbol, symbol);

    private static bool IsReassigned(RuleContext context, SyntaxNode body, ISymbol symbol) =>
        body.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => Refers(context, a.Left, symbol))
        || body.DescendantNodes().OfType<ArgumentSyntax>().Any(a => a.RefKindKeyword.RawKind != 0 && Refers(context, a.Expression, symbol));

    private static bool IsInLoopBody(SyntaxNode site, SyntaxNode body, ISymbol symbol, RuleContext context)
    {
        foreach (var ancestor in site.Ancestors().TakeWhile(a => a != body))
        {
            var isLoop = ancestor is ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForEachStatementSyntax;
            if (!isLoop) continue;
            if (ancestor is ForEachStatementSyntax forEach && forEach.Expression.Span.Contains(site.Span)) continue; // the collection expression runs once
            // A local declared inside the loop is rebuilt each iteration; that is not repeated enumeration of one sequence.
            var declaredInside = symbol is ILocalSymbol && symbol.DeclaringSyntaxReferences.Any(r => ancestor.Span.Contains(r.Span));
            if (!declaredInside) return true;
        }
        return false;
    }

    private static (SyntaxNode First, SyntaxNode Second)? FirstNonExclusivePair(List<SyntaxNode> sites)
    {
        for (var i = 0; i < sites.Count; i++)
            for (var j = i + 1; j < sites.Count; j++)
                if (!AreMutuallyExclusive(sites[i], sites[j])) return (sites[i], sites[j]);
        return null;
    }

    private static bool AreMutuallyExclusive(SyntaxNode a, SyntaxNode b)
    {
        foreach (var ancestor in a.Ancestors())
        {
            if (!ancestor.Span.Contains(b.Span)) continue;
            switch (ancestor)
            {
                case IfStatementSyntax ifs when ifs.Else is not null:
                    var aInThen = ifs.Statement.Span.Contains(a.Span);
                    var bInThen = ifs.Statement.Span.Contains(b.Span);
                    var aInElse = ifs.Else.Span.Contains(a.Span);
                    var bInElse = ifs.Else.Span.Contains(b.Span);
                    if ((aInThen && bInElse) || (aInElse && bInThen)) return true;
                    break;
                case SwitchStatementSyntax sw:
                    var sectionA = sw.Sections.FirstOrDefault(s => s.Span.Contains(a.Span));
                    var sectionB = sw.Sections.FirstOrDefault(s => s.Span.Contains(b.Span));
                    if (sectionA is not null && sectionB is not null && sectionA != sectionB) return true;
                    break;
                case ConditionalExpressionSyntax ce:
                    if ((ce.WhenTrue.Span.Contains(a.Span) && ce.WhenFalse.Span.Contains(b.Span)) || (ce.WhenFalse.Span.Contains(a.Span) && ce.WhenTrue.Span.Contains(b.Span))) return true;
                    break;
            }
        }
        return false;
    }
}
