using Microsoft.CodeAnalysis;

sealed partial class ProjectFragmentBuilder
{
    /// <summary>
    /// Records, for every type declared in this project, which methods implement interface members and which override
    /// virtual/abstract members (including members of other projects and of the framework). Class-hierarchy analysis:
    /// it lists every candidate target in the analyzed solution, which is a superset of what runs for a given receiver.
    /// </summary>
    private (DispatchPair[] Implementations, DispatchPair[] Overrides) CollectDispatchPairs()
    {
        var implementations = new Dictionary<(string, string), DispatchPair>();
        var overrides = new Dictionary<(string, string), DispatchPair>();

        foreach (var type in AllSourceTypes(Compilation.Assembly.GlobalNamespace))
        {
            cancellation.ThrowIfCancellationRequested();
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) continue;
            var typeChain = TypeChain(type);

            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.IsOverride)
                {
                    var implementationId = SymbolIdOf(method);
                    for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
                    {
                        var declared = SymbolIdOf((IMethodSymbol)GraphIdentity.Canonical(overridden));
                        overrides.TryAdd((declared, implementationId), new DispatchPair(declared, implementationId, typeChain));
                    }
                }
            }

            foreach (var contract in type.AllInterfaces)
            {
                foreach (var member in contract.GetMembers().OfType<IMethodSymbol>())
                {
                    if (member.IsStatic) continue;
                    if (type.FindImplementationForInterfaceMember(member) is not IMethodSymbol implementation) continue;
                    var declared = SymbolIdOf((IMethodSymbol)GraphIdentity.Canonical(member));
                    var implementationId = SymbolIdOf((IMethodSymbol)GraphIdentity.Canonical(implementation));
                    implementations.TryAdd((declared, implementationId), new DispatchPair(declared, implementationId, typeChain));
                    EnsureImplementationNode(implementation);
                }
            }
        }

        return (implementations.Values.OrderBy(p => p.Declared, StringComparer.Ordinal).ThenBy(p => p.Implementation, StringComparer.Ordinal).ToArray(),
                overrides.Values.OrderBy(p => p.Declared, StringComparer.Ordinal).ThenBy(p => p.Implementation, StringComparer.Ordinal).ToArray());
    }

    private void EnsureImplementationNode(IMethodSymbol implementation)
    {
        // An implementation inherited from a base class in the same project already has a node; inherited from another
        // project it belongs to that project's fragment. Nothing to add here.
        if (identity.IsSource(implementation) && identity.ProjectOf(implementation) == projectKey) EnsureNode(implementation);
    }

    private string[] TypeChain(INamedTypeSymbol type)
    {
        var chain = new List<string>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            chain.Add(identity.TypeId(current));
        chain.AddRange(type.AllInterfaces.Select(identity.TypeId));
        return chain.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static IEnumerable<INamedTypeSymbol> AllSourceTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
            foreach (var nested in WithNested(type)) yield return nested;
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var type in AllSourceTypes(child)) yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
    {
        if (!type.Locations.Any(l => l.IsInSource)) yield break;
        yield return type;
        foreach (var nested in type.GetTypeMembers())
            foreach (var inner in WithNested(nested)) yield return inner;
    }
}
