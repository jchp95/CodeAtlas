using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

sealed partial class ProjectFragmentBuilder
{
    private static readonly SymbolDisplayFormat SignatureFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private readonly Dictionary<IMethodSymbol, GraphNode> nodeBySymbol = new(SymbolEqualityComparer.Default);

    private GraphNode EnsureNode(IMethodSymbol symbol)
    {
        if (nodeBySymbol.TryGetValue(symbol, out var known)) return known;
        var symbolId = SymbolIdOf(symbol);
        var id = GraphIdentity.NodeId(symbolId);
        if (nodes.TryGetValue(id, out var existing)) return nodeBySymbol[symbol] = existing;

        var canonical = (IMethodSymbol)GraphIdentity.Canonical(symbol);
        var isSource = identity.IsSource(canonical);
        var locations = SourceLocations(canonical);
        var node = new GraphNode(
            id, symbolId, KindOf(canonical), isSource ? identity.ProjectOf(canonical) : "(external)",
            canonical.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor ? canonical.ContainingType.Name : canonical.Name,
            canonical.ToDisplayString(SignatureFormat),
            canonical.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            canonical.ContainingNamespace?.ToDisplayString(),
            canonical.DeclaredAccessibility.ToString().ToLowerInvariant(), !isSource, canonical.IsAbstract,
            canonical.IsVirtual || canonical.IsOverride || canonical.IsAbstract, TagsOf(canonical, isSource),
            locations.FirstOrDefault(), locations.Length > 1 ? locations.Skip(1).ToArray() : null,
            EndLineOf(canonical));
        nodes[id] = node;
        nodeBySymbol[symbol] = node;
        return node;
    }

    private GraphNode EnsureInitializerNode(ITypeSymbol type, bool isStatic)
    {
        var symbolId = $"{identity.SymbolId(type)}/{(isStatic ? "static" : "instance")}-initializers";
        var id = GraphIdentity.NodeId(symbolId);
        if (nodes.TryGetValue(id, out var existing)) return existing;
        var location = type.Locations.Where(l => l.IsInSource).Select(ToLocation).FirstOrDefault();
        var node = new GraphNode(id, symbolId, "initializer", projectKey, isStatic ? "static initializers" : "instance initializers",
            $"{type.Name} {(isStatic ? "static" : "instance")} field/property initializers",
            type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), type.ContainingNamespace?.ToDisplayString(), "private",
            false, false, false, Array.Empty<string>(), location);
        nodes[id] = node;
        return node;
    }

    private GraphNode EnsureAnonymousNode(IAnonymousFunctionOperation lambda, GraphNode owner)
    {
        var key = owner.Id;
        var ordinal = lambdaOrdinals.GetValueOrDefault(key);
        lambdaOrdinals[key] = ordinal + 1;
        var symbolId = $"{owner.SymbolId}/lambda#{ordinal}";
        var id = GraphIdentity.NodeId(symbolId);
        var location = ToLocation(lambda.Syntax.GetLocation());
        var node = new GraphNode(id, symbolId, "lambda", owner.Project, $"lambda#{ordinal}", $"{owner.Name} → lambda#{ordinal}",
            owner.ContainingType, owner.Namespace, "private", false, false, false,
            lambda.Symbol.IsAsync ? new[] { "async" } : Array.Empty<string>(), location, null,
            lambda.Syntax.GetLocation().GetLineSpan().EndLinePosition.Line + 1);
        nodes[id] = node;
        lambdaNodeBySpan[(lambda.Syntax.SyntaxTree.FilePath, lambda.Syntax.SpanStart)] = id;
        return node;
    }

    private readonly Dictionary<IMethodSymbol, string> symbolIds = new(SymbolEqualityComparer.Default);

    private string SymbolIdOf(IMethodSymbol symbol)
    {
        if (symbolIds.TryGetValue(symbol, out var cached)) return cached;
        return symbolIds[symbol] = ComputeSymbolId(symbol);
    }

    private string ComputeSymbolId(IMethodSymbol symbol)
    {
        if (symbol.MethodKind == MethodKind.LocalFunction)
        {
            ISymbol? container = symbol.ContainingSymbol;
            while (container is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }) container = container.ContainingSymbol;
            var parameters = string.Join(",", symbol.Parameters.Select(p => p.Type.ToDisplayString()));
            return $"{(container is null ? "?" : identity.SymbolId(container))}/local:{symbol.Name}({parameters})";
        }
        return identity.SymbolId(symbol);
    }

    private static string KindOf(IMethodSymbol m) => m.MethodKind switch
    {
        MethodKind.Constructor or MethodKind.StaticConstructor => "constructor",
        MethodKind.Destructor => "destructor",
        MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove => "accessor",
        MethodKind.UserDefinedOperator or MethodKind.Conversion => "operator",
        MethodKind.LocalFunction => "localFunction",
        _ => "method"
    };

    private string[] TagsOf(IMethodSymbol method, bool isSource)
    {
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        if (method.IsAsync || method.ReturnType.IsTaskLike()) tags.Add("async");
        if (method.Parameters.Any(p => p.Type.IsCancellationToken())) tags.Add("token-param");
        if (isSource)
        {
            AddTestTags(method, tags);
            if (method.IsImplicitlyDeclared) tags.Add("implicit");
        }
        else
        {
            if (ExternalApiClassifier.IsBlockingWait(method)) tags.Add("blocking");
            if (ExternalApiClassifier.Remote(method) is { } remote) tags.Add("remote:" + remote);
        }
        return tags.ToArray();
    }

    /// <summary>
    /// Test methods are recognized by the resolved attribute class: "test", "test:&lt;framework&gt;", "test-cases:N" (data rows declared
    /// by attributes) and "test-skip:&lt;reason&gt;". Data supplied by a method or file (MemberData, TestCaseSource) is not counted.
    /// </summary>
    private static void AddTestTags(IMethodSymbol method, SortedSet<string> tags)
    {
        var cases = 0;
        string? skip = null;
        foreach (var attribute in method.GetAttributes())
        {
            var name = attribute.AttributeClass?.Name;
            if (name is null) continue;
            if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name[..^"Attribute".Length];
            switch (name)
            {
                case "Fact" or "Theory":
                    tags.Add("test"); tags.Add("test:xunit");
                    if (attribute.NamedArguments.FirstOrDefault(a => a.Key == "Skip").Value.Value is string reason) skip = reason;
                    break;
                case "Test" or "TestCase" or "TestCaseSource":
                    tags.Add("test"); tags.Add("test:nunit");
                    if (name == "TestCase") cases++;
                    break;
                case "TestMethod" or "DataTestMethod":
                    tags.Add("test"); tags.Add("test:mstest");
                    break;
                case "InlineData" or "DataRow": cases++; break;
                case "Ignore": skip = attribute.ConstructorArguments.FirstOrDefault().Value as string ?? "ignored"; break;
            }
        }
        if (tags.Contains("test") && cases > 0) tags.Add($"test-cases:{cases}");
        if (tags.Contains("test") && skip is not null) tags.Add($"test-skip:{skip}");
    }

    private GraphLocation[] SourceLocations(IMethodSymbol symbol)
    {
        var all = symbol.Locations.Concat(symbol.PartialDefinitionPart?.Locations ?? Enumerable.Empty<Location>())
            .Concat(symbol.PartialImplementationPart?.Locations ?? Enumerable.Empty<Location>())
            .Where(l => l.IsInSource).Select(ToLocation).Distinct().ToArray();
        return all;
    }

    private static int EndLineOf(IMethodSymbol symbol) =>
        symbol.DeclaringSyntaxReferences.FirstOrDefault() is { } reference ? reference.GetSyntax().GetLocation().GetLineSpan().EndLinePosition.Line + 1 : 0;

    private static GraphLocation ToLocation(Location location)
    {
        var span = location.GetLineSpan();
        return new GraphLocation(span.Path.Replace('\\', '/'), span.StartLinePosition.Line + 1);
    }
}
