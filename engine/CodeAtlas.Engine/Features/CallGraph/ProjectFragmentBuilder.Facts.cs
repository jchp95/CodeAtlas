using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

sealed partial class ProjectFragmentBuilder
{
    /// <summary>
    /// True when the call returns an IDisposable from a method of this solution and the caller drops it: the result is
    /// discarded, or stored in a local that is neither disposed nor handed elsewhere.
    /// </summary>
    private bool ResultLeaks(IInvocationOperation invocation)
    {
        var returnType = invocation.TargetMethod.ReturnType;
        if (returnType.SpecialType == SpecialType.System_Void || returnType.IsTaskLike() || !returnType.ImplementsDisposable()) return false;
        if (!identity.IsSource(invocation.TargetMethod)) return false;

        if (invocation.Parent is IExpressionStatementOperation) return true;
        if (invocation.Parent is IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator }
            && declarator.Syntax is VariableDeclaratorSyntax syntax)
        {
            if (syntax.Parent?.Parent is UsingStatementSyntax or LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 }) return false;
            var scope = syntax.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax
                or AnonymousFunctionExpressionSyntax or GlobalStatementSyntax);
            if (scope is GlobalStatementSyntax) scope = syntax.SyntaxTree.GetRoot(cancellation);
            return scope is not null && invocation.SemanticModel is { } model && DisposalFlow.Leaks(model, scope, declarator.Symbol, cancellation);
        }
        return false;
    }

    /// <summary>
    /// Compiler nullable-flow warnings (CS8602 dereference, CS8604 argument) whose possibly-null value comes from a call to
    /// a method of this solution that declares a nullable return. The flow analysis is the compiler's; the link to the
    /// producing method is what makes it a cross-method finding.
    /// </summary>
    private void CollectNullFlows(SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot(cancellation);
        foreach (var diagnostic in model.GetDiagnostics(null, cancellation).Where(d => d.Id is "CS8602" or "CS8604"))
        {
            cancellation.ThrowIfCancellationRequested();
            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not { } node) continue;
            var expression = node as ExpressionSyntax ?? node.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
            if (expression is null) continue;

            var producer = expression as InvocationExpressionSyntax;
            if (producer is null && expression is IdentifierNameSyntax identifier && model.GetSymbolInfo(identifier, cancellation).Symbol is ILocalSymbol local
                && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellation) is VariableDeclaratorSyntax { Initializer.Value: InvocationExpressionSyntax initializer })
                producer = initializer;
            if (producer is null || model.GetSymbolInfo(producer, cancellation).Symbol is not IMethodSymbol callee) continue;
            if (!identity.IsSource(callee) || callee.ReturnType.NullableAnnotation != NullableAnnotation.Annotated) continue;
            if (model.GetEnclosingSymbol(expression.SpanStart, cancellation) is not { } enclosing) continue;
            while (enclosing is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction }) enclosing = enclosing.ContainingSymbol;
            if (enclosing is not IMethodSymbol consumer) continue;

            var use = tree.GetLineSpan(expression.Span).StartLinePosition.Line + 1;
            var produced = tree.GetLineSpan(producer.Span).StartLinePosition.Line + 1;
            nullFlows.Add(new NullFlow(identity.NodeIdOf(consumer), identity.NodeIdOf(callee), diagnostic.Id == "CS8604" ? "argument" : "dereference", diagnostic.Id,
                diagnostic.GetMessage(), new GraphLocation(tree.FilePath, use), new GraphLocation(tree.FilePath, produced)));
        }
    }
}
