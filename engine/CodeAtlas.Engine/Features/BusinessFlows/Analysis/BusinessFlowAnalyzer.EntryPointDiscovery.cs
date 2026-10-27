using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static partial class BusinessFlowAnalyzer
{
private static IEnumerable<EntryPoint> DiscoverEntryPoints(
        string repositoryRoot,
        SyntaxTree[] syntaxTrees,
        IReadOnlyDictionary<SyntaxTree, SemanticModel> semanticModels,
        IReadOnlyDictionary<string, string[]> repositoryTypeIndex)
    {
        var verbs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapDelete"] = "DELETE",
            ["MapPatch"] = "PATCH", ["MapHead"] = "HEAD", ["MapOptions"] = "OPTIONS"
        };

        foreach (var tree in syntaxTrees)
        {
            var root = tree.GetRoot();
            var file = RelativeFile(repositoryRoot, tree.FilePath);
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var methodName = InvokedName(invocation.Expression);
                if (!verbs.TryGetValue(methodName, out var verb) || invocation.ArgumentList.Arguments.Count < 2) continue;
                var route = StringValue(invocation.ArgumentList.Arguments[0].Expression);
                if (route is null) continue;
                var handler = invocation.ArgumentList.Arguments[1].Expression;
                if (handler is not LambdaExpressionSyntax && handler is not AnonymousMethodExpressionSyntax)
                {
                    // Method-group handlers are not traced in this version, but are still indexed as routes.
                    handler = null!;
                }
                yield return new EntryPoint($"{verb} {route}", file, Line(invocation), handler, "minimal-api");
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var httpAttribute = method.AttributeLists.SelectMany(list => list.Attributes)
                    .FirstOrDefault(attribute => TryVerb(AttributeName(attribute), out _));
                if (httpAttribute is null) continue;
                _ = TryVerb(AttributeName(httpAttribute), out var verb);
                var classDeclaration = method.Parent?.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                var controllerName = classDeclaration?.Identifier.ValueText ?? "Controller";
                controllerName = controllerName.EndsWith("Controller", StringComparison.Ordinal)
                    ? controllerName[..(controllerName.Length - "Controller".Length)] : controllerName;
                var classRoute = classDeclaration?.AttributeLists.SelectMany(list => list.Attributes)
                    .Where(attribute => AttributeName(attribute) is "Route" or "RouteAttribute")
                    .Select(StringValueFromAttribute).FirstOrDefault(value => value is not null);
                var actionRoute = StringValueFromAttribute(httpAttribute);
                var route = CombineRoute(classRoute, actionRoute, controllerName, method.Identifier.ValueText);
                if (string.IsNullOrWhiteSpace(classRoute) && string.IsNullOrWhiteSpace(actionRoute)) route = "(ruta por resolver)";
                yield return new EntryPoint($"{verb} {route}", file, Line(httpAttribute), method, "controller");
            }

            // Trace MassTransit consumers as independent entry points so cross-project message
            // relations can join a producer path to the consumer implementation.
            foreach (var consumerType in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (consumerType.BaseList is null) continue;
                foreach (var baseType in consumerType.BaseList.Types)
                {
                    var generic = baseType.Type.DescendantNodesAndSelf().OfType<GenericNameSyntax>()
                        .LastOrDefault(candidate => candidate.Identifier.ValueText == "IConsumer"
                            && candidate.TypeArgumentList.Arguments.Count == 1);
                    if (generic is null) continue;

                    var typeText = baseType.Type.ToString();
                    var hasMassTransitContext = typeText.Contains("MassTransit", StringComparison.Ordinal)
                        || root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(usingDirective =>
                            usingDirective.Name?.ToString().StartsWith("MassTransit", StringComparison.Ordinal) == true)
                        || syntaxTrees.SelectMany(candidateTree => candidateTree.GetRoot()
                            .DescendantNodes().OfType<UsingDirectiveSyntax>())
                            .Any(usingDirective => usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)
                                && usingDirective.Name?.ToString().StartsWith("MassTransit", StringComparison.Ordinal) == true);
                    if (!hasMassTransitContext) continue;

                    var contractSyntax = generic.TypeArgumentList.Arguments[0];
                    var semanticModel = semanticModels[tree];
                    var semanticType = semanticModel.GetTypeInfo(contractSyntax).Type;
                    var contract = semanticType is not null && semanticType.TypeKind != TypeKind.Error
                        ? TypeKey(semanticType)
                        : ResolveSourceTypeName(contractSyntax, tree, repositoryTypeIndex)
                            ?? contractSyntax.ToString().Replace("global::", string.Empty, StringComparison.Ordinal);
                    var resolvedInterface = semanticModel.GetTypeInfo(baseType.Type).Type is INamedTypeSymbol consumerInterface
                        && consumerInterface.TypeKind != TypeKind.Error
                        && consumerInterface.Name == "IConsumer"
                        && consumerInterface.ContainingNamespace.ToDisplayString().StartsWith("MassTransit", StringComparison.Ordinal);
                    var consumeMethod = consumerType.Members.OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault(candidate => candidate.Identifier.ValueText is "Consume" or "ConsumeAsync");
                    var handlerNode = (SyntaxNode?)consumeMethod ?? consumerType;
                    yield return new EntryPoint(
                        $"CONSUME {contract}", file, Line(baseType), handlerNode, "message-consumer",
                        contract, consumerType.Identifier.ValueText, resolvedInterface ? "resolved" : "candidate");
                    break;
                }
            }
        }
    }

    private static Dictionary<string, List<INamedTypeSymbol>> BuildDependencyRegistrations(
        SyntaxTree[] trees,
        IReadOnlyDictionary<SyntaxTree, SemanticModel> semanticModels)
    {
        var result = new Dictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);
        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "AddScoped", "AddTransient", "AddSingleton", "AddKeyedScoped", "AddKeyedTransient", "AddKeyedSingleton"
        };

        foreach (var tree in trees)
        {
            var semanticModel = semanticModels[tree];
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var methodName = InvokedName(invocation.Expression);
                if (!supported.Contains(methodName)) continue;
                var generic = invocation.Expression.DescendantNodesAndSelf().OfType<GenericNameSyntax>()
                    .LastOrDefault(name => name.Identifier.ValueText == methodName);
                if (generic is null || generic.TypeArgumentList.Arguments.Count < 2) continue;
                var serviceType = GetNamedType(semanticModel, generic.TypeArgumentList.Arguments[0]);
                var implementationType = GetNamedType(semanticModel, generic.TypeArgumentList.Arguments[1]);
                if (serviceType is null || implementationType is null || serviceType.TypeKind == TypeKind.Error || implementationType.TypeKind == TypeKind.Error) continue;
                AddRegistration(result, TypeKey(serviceType), implementationType);
                AddRegistration(result, serviceType.Name, implementationType);
            }
        }
        return result;
    }

    private static void AddRegistration(Dictionary<string, List<INamedTypeSymbol>> registrations, string key, INamedTypeSymbol implementation)
    {
        if (!registrations.TryGetValue(key, out var list)) registrations[key] = list = [];
        if (!list.Any(item => SymbolEqualityComparer.Default.Equals(item, implementation))) list.Add(implementation);
    }
}
