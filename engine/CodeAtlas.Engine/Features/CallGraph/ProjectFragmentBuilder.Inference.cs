using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

sealed partial class ProjectFragmentBuilder
{
    private Dictionary<string, List<(ClassDeclarationSyntax Type, SemanticModel Model)>>? classesByName;

    /// <summary>
    /// Mediator Send(request): the edge is recorded with the static request type; the assembler links it to the
    /// IRequestHandler implementations of that type found anywhere in the solution. Symbol-based only: a class that merely
    /// has a "…Handler" name is not linked.
    /// </summary>
    private void TryMediator(GraphNode owner, IInvocationOperation invocation, Flags flags)
    {
        var target = invocation.TargetMethod;
        if (target.Name is not ("Send" or "SendAsync") || target.ContainingType.Name is not ("ISender" or "IMediator" or "Mediator")) return;
        var request = invocation.Arguments.FirstOrDefault()?.Value.Unwrap().Type;
        if (request is null || request.TypeKind is TypeKind.Error or TypeKind.Interface || request.SpecialType != SpecialType.None) return;
        AddEdge(owner, null, EdgeResolution.MediatorDispatch, ToLocation(invocation.Syntax.GetLocation()), flags.InLoop, flags.InTry, false, false, false, identity.TypeId(request),
            "mediator request handler lookup", EdgeCertainty.Potential, label: request.Name);
    }

    private DispatchPair[] CollectHandlers()
    {
        var pairs = new Dictionary<(string, string), DispatchPair>();
        foreach (var type in AllSourceTypes(Compilation.Assembly.GlobalNamespace))
        {
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) continue;
            foreach (var contract in type.AllInterfaces.Where(i => i.Name == "IRequestHandler" && i.TypeArguments.Length > 0 && i.TypeArguments[0].TypeKind != TypeKind.Error))
            {
                var request = identity.TypeId(contract.TypeArguments[0]);
                foreach (var member in contract.GetMembers().OfType<IMethodSymbol>())
                    if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation)
                    {
                        var id = SymbolIdOf((IMethodSymbol)GraphIdentity.Canonical(implementation));
                        pairs.TryAdd((request, id), new DispatchPair(request, id, Array.Empty<string>()));
                        EnsureNode(implementation);
                    }
            }
        }
        return pairs.Values.OrderBy(p => p.Declared, StringComparer.Ordinal).ThenBy(p => p.Implementation, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// A call Roslyn cannot bind (typically because a framework package is missing and poisons the receiver's type) is
    /// matched to the one source method whose declaring type name, method name and arity agree. The result is recorded as
    /// <c>SyntaxInferred</c> and potential; it never replaces the unresolved edge, so the graph still reports itself incomplete.
    /// </summary>
    private void TrySyntaxFallback(GraphNode owner, InvocationExpressionSyntax syntax, Flags flags)
    {
        if (syntax.Expression is not MemberAccessExpressionSyntax access || !models.TryGetValue(syntax.SyntaxTree, out var model)) return;
        var receiverName = ReceiverTypeName(access.Expression, syntax, model, out var receiverNamespace);
        if (string.IsNullOrWhiteSpace(receiverName)) return;

        classesByName ??= models.SelectMany(pair => pair.Key.GetRoot(cancellation).DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => (Type: c, Model: pair.Value)))
            .GroupBy(c => c.Type.Identifier.ValueText, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (!classesByName.TryGetValue(receiverName, out var classes)) return;

        var methodName = access.Name.Identifier.ValueText;
        var matches = classes.Where(c => receiverNamespace is null || NamespaceOf(c.Type) == receiverNamespace)
            .SelectMany(c => c.Type.Members.OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.ValueText == methodName && m.ParameterList.Parameters.Count == syntax.ArgumentList.Arguments.Count)
                .Select(m => c.Model.GetDeclaredSymbol(m, cancellation)).OfType<IMethodSymbol>())
            .DistinctBy(m => SymbolIdOf(m)).Take(2).ToArray();
        if (matches.Length != 1) return; // ambiguity is never resolved by guessing

        var targetId = SymbolIdOf(matches[0]);
        EnsureNode(matches[0]);
        AddEdge(owner, targetId, EdgeResolution.SyntaxInferred, ToLocation(syntax.GetLocation()), flags.InLoop, flags.InTry, false, false, false, null,
            "Roslyn could not bind this call; matched by receiver type name, method name and argument count", EdgeCertainty.Potential);
    }

    private static string? NamespaceOf(SyntaxNode type) =>
        string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse()) is { Length: > 0 } ns ? ns : null;

    private string? ReceiverTypeName(ExpressionSyntax receiver, SyntaxNode context, SemanticModel model, out string? ns)
    {
        ns = null;
        if (model.GetTypeInfo(receiver, cancellation).Type is { TypeKind: not TypeKind.Error } type) { ns = type.ContainingNamespace?.ToDisplayString(); return type.Name; }
        var symbolType = model.GetSymbolInfo(receiver, cancellation).Symbol switch
        {
            IParameterSymbol p => p.Type, ILocalSymbol l => l.Type, IFieldSymbol f => f.Type, IPropertySymbol pr => pr.Type, _ => null
        };
        if (symbolType is { TypeKind: not TypeKind.Error }) { ns = symbolType.ContainingNamespace?.ToDisplayString(); return symbolType.Name; }
        // Primary-constructor parameters can lose their type when unrelated references are missing: recover the declared type text only.
        if (receiver is IdentifierNameSyntax identifier
            && context.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.ParameterList?.Parameters
                .FirstOrDefault(p => p.Identifier.ValueText == identifier.Identifier.ValueText)?.Type?.ToString() is { } declared)
            return declared.Split('.').Last();
        return null;
    }
}
