using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

sealed partial class RepositoryScanner
{
private static void DetectDotnetEndpoints(SyntaxTree tree, string relativeFile, List<string> endpoints,
        List<ArchitectureEvidence> codeEvidence)
    {
        var root = tree.GetRoot();
        var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Get", "Post", "Put", "Delete", "Patch", "Head", "Options" };

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var method = GetInvokedMethodName(invocation.Expression);
            if (!method.StartsWith("Map", StringComparison.Ordinal) || !verbs.Contains(method[3..])) continue;
            var route = GetFirstStringArgument(invocation.ArgumentList);
            if (route is null) continue;
            var line = GetLine(invocation);
            var endpoint = $"{method[3..].ToUpperInvariant()} {route}";
            endpoints.Add($"{endpoint} · Minimal API · {relativeFile}");
            codeEvidence.Add(new ArchitectureEvidence("api-endpoint", endpoint, relativeFile, line, "candidate",
                "Mapeado HTTP de Minimal API detectado sintácticamente."));
        }

        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var attributeName = GetSyntaxName(attribute.Name);
            if (attributeName.StartsWith("Http", StringComparison.OrdinalIgnoreCase))
            {
                var verb = attributeName.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase)
                    ? attributeName[..^9] : attributeName;
                verb = verb.Length > 4 ? verb[4..] : string.Empty;
                if (!verbs.Contains(verb)) continue;
                var route = GetFirstStringArgument(attribute.ArgumentList) ?? "(ruta por resolver)";
                var endpoint = $"{verb.ToUpperInvariant()} {route}";
                endpoints.Add($"{endpoint} · Controller · {relativeFile}");
                codeEvidence.Add(new ArchitectureEvidence("api-endpoint", endpoint, relativeFile, GetLine(attribute), "candidate",
                    "Atributo HTTP de controlador detectado sintácticamente; la ruta efectiva puede combinarse con [Route]."));
            }
            else if (attributeName is "Route" or "RouteAttribute")
            {
                var route = GetFirstStringArgument(attribute.ArgumentList);
                if (route is not null) endpoints.Add($"ROUTE {route} · Controller base · {relativeFile}");
            }
        }
    }

    private static void DetectMessaging(SyntaxTree tree, SemanticModel semanticModel, string relativeFile,
        List<string> published, List<string> consumed, List<string> publishedEvidence, List<string> consumedEvidence,
        List<ArchitectureEvidence> codeEvidence)
    {
        var root = tree.GetRoot();

        foreach (var baseType in root.DescendantNodes().OfType<SimpleBaseTypeSyntax>())
        {
            var generic = GetRightmostGenericName(baseType.Type);
            if (generic is null || generic.Identifier.ValueText != "IConsumer" || generic.TypeArgumentList.Arguments.Count != 1)
                continue;

            var consumerSymbol = semanticModel.GetTypeInfo(baseType.Type).Type as INamedTypeSymbol;
            var consumerResolved = consumerSymbol is not null && consumerSymbol.TypeKind != TypeKind.Error
                && consumerSymbol.Name == "IConsumer"
                && consumerSymbol.ContainingNamespace.ToDisplayString().StartsWith("MassTransit", StringComparison.Ordinal);
            if (consumerSymbol is not null && consumerSymbol.TypeKind != TypeKind.Error && !consumerResolved)
                continue;

            var contractSyntax = generic.TypeArgumentList.Arguments[0];
            var semanticName = GetSemanticTypeName(semanticModel, contractSyntax);
            var contract = semanticName ?? NormalizeTypeName(contractSyntax.ToString());
            if (contract.Length == 0) continue;
            var line = GetLine(baseType);
            consumed.Add(contract);
            consumedEvidence.Add($"{contract}|{relativeFile}:{line}");
            codeEvidence.Add(new ArchitectureEvidence("message-consumed", contract, relativeFile, line,
                semanticName is not null && consumerResolved ? "resolved" : "candidate",
                "Interfaz IConsumer<T> detectada; confirma la resolución de MassTransit y del contrato."));
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var method = GetInvokedMethodName(invocation.Expression);
            if (method is not ("Publish" or "PublishAsync" or "Send" or "SendAsync")) continue;

            var resolvedMethod = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (resolvedMethod is not null && !LooksLikeMessagingMethod(resolvedMethod)) continue;

            string? contract = null;
            TypeSyntax? contractSyntax = null;
            var genericName = GetGenericInvokedName(invocation.Expression);
            if (genericName is not null && genericName.TypeArgumentList.Arguments.Count > 0)
            {
                contractSyntax = genericName.TypeArgumentList.Arguments[0];
                contract = GetSemanticTypeName(semanticModel, contractSyntax) ?? NormalizeTypeName(contractSyntax.ToString());
            }
            else
            {
                var creation = invocation.ArgumentList.Arguments
                    .SelectMany(argument => argument.Expression.DescendantNodesAndSelf())
                    .OfType<ObjectCreationExpressionSyntax>()
                    .FirstOrDefault();
                if (creation is not null)
                {
                    contractSyntax = creation.Type;
                    contract = GetSemanticTypeName(semanticModel, creation) ?? NormalizeTypeName(creation.Type.ToString());
                }
                else if (invocation.ArgumentList.Arguments.FirstOrDefault() is { } firstArgument)
                {
                    var argumentType = semanticModel.GetTypeInfo(firstArgument.Expression).Type;
                    if (argumentType is not null && argumentType.TypeKind != TypeKind.Error && argumentType.SpecialType != SpecialType.System_String)
                        contract = NormalizeSymbolName(argumentType);
                }
            }

            if (string.IsNullOrWhiteSpace(contract)) continue;
            var line = GetLine(invocation);
            published.Add(contract);
            publishedEvidence.Add($"{contract}|{relativeFile}:{line}");
            var contractResolved = contractSyntax is not null
                ? GetSemanticTypeName(semanticModel, contractSyntax) is not null
                : invocation.ArgumentList.Arguments.FirstOrDefault() is { } argument
                    && semanticModel.GetTypeInfo(argument.Expression).Type is { TypeKind: not TypeKind.Error };
            var publishResolved = resolvedMethod is not null && LooksLikeMessagingMethod(resolvedMethod);
            codeEvidence.Add(new ArchitectureEvidence("message-published", contract, relativeFile, line,
                contractResolved && publishResolved ? "resolved" : "candidate",
                $"Llamada {method} detectada; validar la configuración de enrutamiento y transporte."));
        }
    }

    private static bool LooksLikeMessagingMethod(IMethodSymbol method)
    {
        var namespaceName = method.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var typeName = method.ContainingType?.Name ?? string.Empty;
        return namespaceName.StartsWith("MassTransit", StringComparison.Ordinal)
            || typeName.Contains("PublishEndpoint", StringComparison.Ordinal)
            || typeName.Contains("SendEndpoint", StringComparison.Ordinal)
            || typeName.Contains("ConsumeContext", StringComparison.Ordinal);
    }

    private static string? GetSemanticTypeName(SemanticModel model, TypeSyntax typeSyntax)
    {
        var type = model.GetTypeInfo(typeSyntax).Type;
        return type is null || type.TypeKind == TypeKind.Error ? null : NormalizeSymbolName(type);
    }

    private static string? GetSemanticTypeName(SemanticModel model, ObjectCreationExpressionSyntax expression)
    {
        var type = model.GetTypeInfo(expression).Type;
        return type is null || type.TypeKind == TypeKind.Error ? null : NormalizeSymbolName(type);
    }

    private static string NormalizeSymbolName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal);

    private static int GetLine(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static void DetectDependencyInjection(SyntaxTree tree, SemanticModel semanticModel, string relativeFile,
        List<ArchitectureEvidence> codeEvidence)
    {
        var root = tree.GetRoot();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var method = GetInvokedMethodName(invocation.Expression);
            if (method is not ("AddScoped" or "AddTransient" or "AddSingleton" or "AddKeyedScoped" or "AddKeyedTransient" or "AddKeyedSingleton"))
                continue;
            var resolvedMethod = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (resolvedMethod is not null && !resolvedMethod.ContainingNamespace.ToDisplayString()
                    .StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal)) continue;
            var generic = GetGenericInvokedName(invocation.Expression);
            if (generic is null || generic.TypeArgumentList.Arguments.Count == 0) continue;
            var types = generic.TypeArgumentList.Arguments;
            var serviceName = GetSemanticTypeName(semanticModel, types[0]) ?? NormalizeTypeName(types[0].ToString());
            var implementationName = types.Count > 1
                ? GetSemanticTypeName(semanticModel, types[1]) ?? NormalizeTypeName(types[1].ToString())
                : serviceName;
            var line = GetLine(invocation);
            var typeSymbolsResolved = GetSemanticTypeName(semanticModel, types[0]) is not null
                && (types.Count == 1 || GetSemanticTypeName(semanticModel, types[1]) is not null);
            codeEvidence.Add(new ArchitectureEvidence("service-registration", serviceName, relativeFile, line,
                resolvedMethod is not null && typeSymbolsResolved ? "resolved" : "candidate",
                $"{method}: implementación {implementationName}."));
        }

        // Ordinary constructors.
        foreach (var constructor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            foreach (var parameter in constructor.ParameterList.Parameters)
            {
                if (parameter.Type is null) continue;
                AddConstructorParameterEvidence(semanticModel, parameter, constructor.Identifier.ValueText,
                    relativeFile, codeEvidence, isPrimary: false);
            }
        }

        // C# 12 primary constructors are declared on the type declaration, not as
        // ConstructorDeclarationSyntax nodes. Handle classes and structs explicitly;
        // positional records are intentionally omitted because their parameters are
        // commonly data-model state rather than container-injected dependencies.
        foreach (var typeDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (typeDeclaration.ParameterList is null) continue;
            foreach (var parameter in typeDeclaration.ParameterList.Parameters)
            {
                if (parameter.Type is null) continue;
                AddConstructorParameterEvidence(semanticModel, parameter, typeDeclaration.Identifier.ValueText,
                    relativeFile, codeEvidence, isPrimary: true);
            }
        }
        foreach (var typeDeclaration in root.DescendantNodes().OfType<StructDeclarationSyntax>())
        {
            if (typeDeclaration.ParameterList is null) continue;
            foreach (var parameter in typeDeclaration.ParameterList.Parameters)
            {
                if (parameter.Type is null) continue;
                AddConstructorParameterEvidence(semanticModel, parameter, typeDeclaration.Identifier.ValueText,
                    relativeFile, codeEvidence, isPrimary: true);
            }
        }
    }

    private static void AddConstructorParameterEvidence(SemanticModel semanticModel, ParameterSyntax parameter,
        string ownerName, string relativeFile, List<ArchitectureEvidence> codeEvidence, bool isPrimary)
    {
        if (parameter.Type is null) return;
        var semanticName = GetSemanticTypeName(semanticModel, parameter.Type);
        var dependencyName = semanticName ?? NormalizeTypeName(parameter.Type.ToString());
        var line = GetLine(parameter);
        codeEvidence.Add(new ArchitectureEvidence("constructor-injection", dependencyName, relativeFile, line,
            semanticName is null ? "candidate" : "resolved",
            isPrimary
                ? $"Constructor primario de {ownerName} recibe {dependencyName}; confirmar que su registro corresponde a DI."
                : $"Constructor de {ownerName} recibe {dependencyName} por parámetro."));
    }

    private static string NormalizeContractName(string value) =>
        Regex.Replace(value.Replace("global::", string.Empty, StringComparison.Ordinal), @"\s+", string.Empty).TrimEnd('?');

    private static string[] NormalizeContractCandidates(IEnumerable<string> contracts)
    {
        var normalized = contracts.Select(NormalizeContractName).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var fullyQualified = normalized.Where(c => c.Contains('.')).ToArray();
        if (fullyQualified.Length == 0) return normalized.OrderBy(c => c, StringComparer.Ordinal).ToArray();

        // When semantic information resolves a short syntax-only candidate to a qualified symbol,
        // prefer the qualified symbol for that short name. Keep different namespaces distinct.
        return normalized.Where(candidate => candidate.Contains('.')
                || !fullyQualified.Any(qualified => string.Equals(TypeNames.Short(qualified), candidate, StringComparison.Ordinal)))
            .OrderBy(c => c, StringComparer.Ordinal).ToArray();
    }

    private static bool ContractNamesMatch(string first, string second, ProjectNode[] projects)
    {
        var normalizedFirst = NormalizeContractName(first);
        var normalizedSecond = NormalizeContractName(second);
        if (string.Equals(normalizedFirst, normalizedSecond, StringComparison.Ordinal)) return true;
        var shortFirst = TypeNames.Short(normalizedFirst);
        var shortSecond = TypeNames.Short(normalizedSecond);
        if (!string.Equals(shortFirst, shortSecond, StringComparison.Ordinal)) return false;

        var qualifiedCandidates = projects.SelectMany(project => project.ContractsPublished.Concat(project.ContractsConsumed))
            .Select(NormalizeContractName)
            .Where(name => name.Contains('.')
                && string.Equals(TypeNames.Short(name), shortFirst, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // Only use short-name fallback when the scan has not found competing qualified symbols.
        return qualifiedCandidates.Length <= 1;
    }

    private static void AddContract(string contract, string relativeFile, List<string> contracts, List<string> evidence)
    {
        if (string.IsNullOrWhiteSpace(contract)) return;
        contracts.Add(contract);
        evidence.Add($"{contract}|{relativeFile}");
    }

    private static string GetInvokedMethodName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => GetSyntaxName(member.Name),
        SimpleNameSyntax simple => GetSyntaxName(simple),
        _ => string.Empty
    };

    private static GenericNameSyntax? GetGenericInvokedName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name as GenericNameSyntax,
        GenericNameSyntax generic => generic,
        _ => null
    };

    private static GenericNameSyntax? GetRightmostGenericName(TypeSyntax type) => type switch
    {
        GenericNameSyntax generic => generic,
        QualifiedNameSyntax qualified when qualified.Right is GenericNameSyntax generic => generic,
        AliasQualifiedNameSyntax alias when alias.Name is GenericNameSyntax generic => generic,
        _ => null
    };

    private static string GetSyntaxName(SyntaxNode node) => node switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => GetSyntaxName(qualified.Right),
        AliasQualifiedNameSyntax alias => GetSyntaxName(alias.Name),
        _ => node.ToString().Split('.').LastOrDefault() ?? string.Empty
    };

    private static string? GetFirstStringArgument(AttributeArgumentListSyntax? arguments) =>
        arguments?.Arguments.Where(a => a.NameEquals is null && a.NameColon is null)
            .Select(a => a.Expression).OfType<LiteralExpressionSyntax>()
            .FirstOrDefault(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))?.Token.ValueText;

    private static string? GetFirstStringArgument(ArgumentListSyntax arguments) =>
        arguments.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>()
            .FirstOrDefault(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))?.Token.ValueText;

    private static string NormalizeTypeName(string value) => value.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
}
