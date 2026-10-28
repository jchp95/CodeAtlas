using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Extracts nodes, call sites and dispatch facts of one project from real Roslyn symbols and <see cref="IOperation"/>
/// trees. Cross-project and virtual targets are only recorded here as declared members; the global fan-out to
/// implementations happens when fragments are assembled, which is what allows a fragment to be cached on its own.
/// </summary>
sealed partial class ProjectFragmentBuilder
{
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod", "FactAttribute", "TheoryAttribute",
        "TestAttribute", "TestMethodAttribute"
    };

    private readonly LoadedDotnetProject project;
    private readonly string projectKey;
    private readonly GraphIdentity identity;
    private readonly Lazy<IReadOnlyDictionary<string, string[]>> typeIndex;
    private readonly string repositoryRoot;
    private readonly CancellationToken cancellation;
    private readonly Dictionary<string, GraphNode> nodes = new(StringComparer.Ordinal);
    private readonly List<RawEdge> edges = new();
    private readonly Dictionary<string, int> siteOrdinals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> lambdaOrdinals = new(StringComparer.Ordinal);
    private readonly Dictionary<(string File, int Start), string> lambdaNodeBySpan = new();
    private readonly Dictionary<string, bool> acceptsTokenCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> disposableCreators = new(StringComparer.Ordinal);
    private readonly List<string> warnings = new();
    private readonly Dictionary<string, (bool IsStatic, INamedTypeSymbol Type)> typesWithInitializers = new(StringComparer.Ordinal);

    public ProjectFragmentBuilder(string repositoryRoot, LoadedDotnetProject project, string projectKey, GraphIdentity identity,
        Lazy<IReadOnlyDictionary<string, string[]>> typeIndex, CancellationToken cancellation)
    {
        this.repositoryRoot = repositoryRoot;
        this.project = project;
        this.projectKey = projectKey;
        this.identity = identity;
        this.typeIndex = typeIndex;
        this.cancellation = cancellation;
    }

    private Compilation Compilation => project.Unit.Compilation;

    public ProjectFragment Build()
    {
        var models = project.Unit.SyntaxTrees.ToDictionary(tree => tree, tree => Compilation.GetSemanticModel(tree, ignoreAccessibility: true));
        foreach (var (tree, model) in models)
        {
            cancellation.ThrowIfCancellationRequested();
            DeclareMembers(tree, model);
        }
        LinkInitializers();

        var entries = DiscoverEntryPoints(models);
        var (implementations, overrides) = CollectDispatchPairs();

        foreach (var entry in entries)
        {
            if (!nodes.TryGetValue(entry.NodeId, out var node)) continue;
            var tag = entry.Kind == "message-consumer" ? "entry:consumer" : "entry:http";
            if (!node.Tags.Contains(tag)) nodes[entry.NodeId] = node with { Tags = node.Tags.Append(tag).OrderBy(t => t, StringComparer.Ordinal).ToArray() };
        }
        foreach (var id in disposableCreators.Where(nodes.ContainsKey))
            nodes[id] = nodes[id] with { Tags = nodes[id].Tags.Append("disposable-creator").Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray() };

        return new ProjectFragment(projectKey,
            nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToArray(), edges.ToArray(),
            implementations, overrides, entries.ToArray(), warnings.ToArray());
    }

    private void DeclareMembers(SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot(cancellation);
        foreach (var syntax in root.DescendantNodes())
        {
            cancellation.ThrowIfCancellationRequested();
            switch (syntax)
            {
                case BaseMethodDeclarationSyntax declaration:
                    if (model.GetDeclaredSymbol(declaration, cancellation) is IMethodSymbol method)
                    {
                        var owner = EnsureNode(method);
                        if (model.GetOperation(declaration, cancellation) is { } body) Walk(owner, body, tree);
                    }
                    break;
                case AccessorDeclarationSyntax accessor:
                    if (model.GetDeclaredSymbol(accessor, cancellation) is IMethodSymbol accessorSymbol)
                    {
                        var owner = EnsureNode(accessorSymbol);
                        if (model.GetOperation(accessor, cancellation) is { } body) Walk(owner, body, tree);
                    }
                    break;
                case PropertyDeclarationSyntax property:
                    DeclareProperty(property, model, tree);
                    break;
                case VariableDeclaratorSyntax { Initializer: { } initializer, Parent: VariableDeclarationSyntax { Parent: FieldDeclarationSyntax field } }:
                    DeclareInitializer(initializer, field.Modifiers.Any(SyntaxKind.StaticKeyword), model, tree, field);
                    break;
                case GlobalStatementSyntax global:
                    DeclareTopLevelStatement(global, model, tree);
                    break;
            }
        }
    }

    private void DeclareProperty(PropertyDeclarationSyntax property, SemanticModel model, SyntaxTree tree)
    {
        if (model.GetDeclaredSymbol(property, cancellation) is not IPropertySymbol symbol) return;
        // Auto-property accessors have no accessor syntax body but are still call targets (impact of changing a DTO property).
        if (symbol.GetMethod is { } getter) EnsureNode(getter);
        if (symbol.SetMethod is { } setter) EnsureNode(setter);
        if (property.ExpressionBody is { } expression && symbol.GetMethod is { } expressionGetter
            && model.GetOperation(expression.Expression, cancellation) is { } body)
            Walk(EnsureNode(expressionGetter), body, tree);
        if (property.Initializer is { } initializer)
            DeclareInitializer(initializer, symbol.IsStatic, model, tree, property);
    }

    private void DeclareInitializer(EqualsValueClauseSyntax initializer, bool isStatic, SemanticModel model, SyntaxTree tree, SyntaxNode memberSyntax)
    {
        var enclosing = model.GetEnclosingSymbol(memberSyntax.SpanStart, cancellation);
        var containing = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;
        if (containing is null || model.GetOperation(initializer, cancellation) is not { } operation) return;
        var typeId = identity.SymbolId(containing);
        var owner = EnsureInitializerNode(containing, isStatic);
        typesWithInitializers[typeId + (isStatic ? "#s" : "#i")] = (isStatic, containing);
        Walk(owner, operation, tree);
    }

    private void DeclareTopLevelStatement(GlobalStatementSyntax global, SemanticModel model, SyntaxTree tree)
    {
        if (model.GetEnclosingSymbol(global.SpanStart, cancellation) is not IMethodSymbol main) return;
        var owner = EnsureNode(main);
        if (model.GetOperation(global.Statement, cancellation) is { } operation) Walk(owner, operation, tree);
    }

    /// <summary>Field/property initializers run as part of every instance constructor that does not delegate with this(...).</summary>
    private void LinkInitializers()
    {
        foreach (var (key, (isStatic, type)) in typesWithInitializers.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var initializer = EnsureInitializerNode(type, isStatic);
            var constructors = isStatic ? type.StaticConstructors : type.InstanceConstructors;
            foreach (var constructor in constructors.Where(c => !DelegatesToThis(c)))
            {
                var owner = EnsureNode(constructor);
                AddEdge(owner, initializer.SymbolId, EdgeResolution.Direct, owner.Location ?? new GraphLocation("(unknown)", 1), false, false, false, false, false, null,
                    "field/property initializers");
            }
        }
    }

    private static bool DelegatesToThis(IMethodSymbol constructor) =>
        constructor.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<ConstructorDeclarationSyntax>()
            .Any(c => c.Initializer?.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword) == true);

    private IReadOnlyList<GraphEntryPoint> DiscoverEntryPoints(Dictionary<SyntaxTree, SemanticModel> models)
    {
        var discovered = BusinessFlowAnalyzer.DiscoverSharedEntryPoints(repositoryRoot, project.Unit.SyntaxTrees, models, typeIndex.Value);
        var result = new List<GraphEntryPoint>();
        foreach (var entry in discovered)
        {
            string? nodeId = null;
            switch (entry.Handler)
            {
                case MethodDeclarationSyntax method when models.TryGetValue(method.SyntaxTree, out var model)
                                                        && model.GetDeclaredSymbol(method, cancellation) is IMethodSymbol symbol:
                    nodeId = identity.NodeIdOf(symbol);
                    break;
                case { } lambda when lambdaNodeBySpan.TryGetValue((lambda.SyntaxTree.FilePath, lambda.SpanStart), out var lambdaId):
                    nodeId = lambdaId;
                    break;
            }
            if (nodeId is null) continue;
            result.Add(new GraphEntryPoint(entry.Kind, entry.Label, nodeId, entry.File, entry.Line, entry.Contract));
        }
        return result.DistinctBy(e => (e.NodeId, e.Label)).OrderBy(e => e.Label, StringComparer.Ordinal).ThenBy(e => e.NodeId, StringComparer.Ordinal).ToArray();
    }
}
