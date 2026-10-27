using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static partial class BusinessFlowAnalyzer
{
private static bool IsMessagePublish(IMethodSymbol? method, string methodName)
    {
        if (methodName is not ("Publish" or "PublishAsync")) return false;
        if (method is null) return true;
        var namespaceName = method.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var typeName = method.ContainingType?.Name ?? string.Empty;
        return namespaceName.StartsWith("MassTransit", StringComparison.Ordinal)
            || typeName.Contains("PublishEndpoint", StringComparison.Ordinal)
            || typeName.Contains("SendEndpoint", StringComparison.Ordinal)
            || typeName.Contains("ConsumeContext", StringComparison.Ordinal);
    }

    private static string? FindMessageContract(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, string[]> repositoryTypeIndex)
    {
        var generic = invocation.Expression.DescendantNodesAndSelf().OfType<GenericNameSyntax>().LastOrDefault();
        if (generic is not null && generic.TypeArgumentList.Arguments.Count > 0)
        {
            var typeSyntax = generic.TypeArgumentList.Arguments[0];
            var type = GetNamedType(semanticModel, typeSyntax);
            if (type is not null && type.TypeKind != TypeKind.Error) return TypeKey(type);
            return ResolveSourceTypeName(typeSyntax, invocation.SyntaxTree, repositoryTypeIndex) ?? typeSyntax.ToString();
        }

        var creation = invocation.ArgumentList.Arguments.SelectMany(argument => argument.Expression.DescendantNodesAndSelf())
            .OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
        if (creation is null) return null;
        var creationType = semanticModel.GetTypeInfo(creation).Type ?? semanticModel.GetSymbolInfo(creation.Type).Symbol as ITypeSymbol;
        return creationType is not null && creationType.TypeKind != TypeKind.Error
            ? TypeKey(creationType)
            : ResolveSourceTypeName(creation.Type, invocation.SyntaxTree, repositoryTypeIndex) ?? creation.Type.ToString();
    }

    private static string? ResolveSourceTypeName(
        TypeSyntax typeSyntax,
        SyntaxTree syntaxTree,
        IReadOnlyDictionary<string, string[]> repositoryTypeIndex)
    {
        var writtenName = typeSyntax.ToString().Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
        var simpleName = writtenName.Split('.').LastOrDefault() ?? writtenName;
        if (repositoryTypeIndex.TryGetValue(simpleName, out var candidates))
        {
            if (candidates.Length == 1) return candidates[0];

            var root = syntaxTree.GetRoot();
            var imports = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(usingDirective => usingDirective.Alias is null && usingDirective.Name is not null)
                .Select(usingDirective => usingDirective.Name!.ToString().Replace("global::", string.Empty, StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            var importedCandidates = candidates.Where(candidate =>
            {
                var separator = candidate.LastIndexOf('.');
                var candidateNamespace = separator < 0 ? string.Empty : candidate[..separator];
                return imports.Contains(candidateNamespace);
            }).Distinct(StringComparer.Ordinal).ToArray();
            if (importedCandidates.Length == 1) return importedCandidates[0];
        }

        // If the source explicitly qualifies a type, retain that spelling when it matches
        // a known declaration; otherwise leave the evidence as source text rather than guessing.
        if (writtenName.Contains(".", StringComparison.Ordinal)
            && repositoryTypeIndex.TryGetValue(simpleName, out var qualifiedCandidates)
            && qualifiedCandidates.Contains(writtenName, StringComparer.Ordinal))
            return writtenName;

        return null;
    }

    private static bool LooksLikeDependencyBoundary(string typeName) =>
        typeName.EndsWith("Client", StringComparison.OrdinalIgnoreCase)
        || typeName.EndsWith("Gateway", StringComparison.OrdinalIgnoreCase)
        || typeName.EndsWith("Repository", StringComparison.OrdinalIgnoreCase)
        || typeName.EndsWith("Store", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Endpoint", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePersistence(IMethodSymbol method, string typeName) =>
        typeName.Contains("DbContext", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Repository", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("UnitOfWork", StringComparison.OrdinalIgnoreCase)
        || method.Name.StartsWith("SaveChanges", StringComparison.Ordinal)
        || method.Name.StartsWith("ExecuteSql", StringComparison.Ordinal);

    private static bool HasExecutableBody(IMethodSymbol method) => FindMethodDeclaration(method) is { } declaration
        && (declaration.Body is not null || declaration.ExpressionBody is not null);

    private static MethodDeclarationSyntax? FindMethodDeclaration(IMethodSymbol method) => method.DeclaringSyntaxReferences
        .Select(reference => reference.GetSyntax()).OfType<MethodDeclarationSyntax>().FirstOrDefault();

    private static INamedTypeSymbol? GetNamedType(SemanticModel semanticModel, TypeSyntax syntax) =>
        semanticModel.GetTypeInfo(syntax).Type as INamedTypeSymbol
        ?? semanticModel.GetSymbolInfo(syntax).Symbol as INamedTypeSymbol;

    private static string TypeKey(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal);

    private static string MethodKey(IMethodSymbol method) => method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string InvokedName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => member.Name.ToString()
        },
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => expression.ToString().Split('(')[0].Split('.').Last()
    };

    private static string AttributeName(AttributeSyntax attribute) =>
        attribute.Name switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            _ => attribute.Name.ToString().Split('.').Last()
        };

    private static bool TryVerb(string attributeName, out string verb)
    {
        var name = attributeName.EndsWith("Attribute", StringComparison.Ordinal)
            ? attributeName[..^"Attribute".Length] : attributeName;
        verb = name switch
        {
            "HttpGet" => "GET", "HttpPost" => "POST", "HttpPut" => "PUT", "HttpDelete" => "DELETE",
            "HttpPatch" => "PATCH", "HttpHead" => "HEAD", "HttpOptions" => "OPTIONS", _ => string.Empty
        };
        return verb.Length > 0;
    }

    private static string? StringValueFromAttribute(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is { } expression ? StringValue(expression) : null;

    private static string? StringValue(ExpressionSyntax expression) => expression is LiteralExpressionSyntax literal
        && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null;

    private static string CombineRoute(string? classRoute, string? actionRoute, string controller, string action)
    {
        var classPart = (classRoute ?? string.Empty).Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)
            .Replace("[action]", action, StringComparison.OrdinalIgnoreCase).Trim('/');
        var actionPart = (actionRoute ?? string.Empty).Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)
            .Replace("[action]", action, StringComparison.OrdinalIgnoreCase).Trim();
        if (actionPart.StartsWith("~/", StringComparison.Ordinal)) return "/" + actionPart[2..].Trim('/');
        if (actionPart.StartsWith("/", StringComparison.Ordinal)) return actionPart;
        var joined = string.Join("/", new[] { classPart, actionPart.Trim('/') }.Where(part => part.Length > 0));
        return "/" + joined;
    }

    private static string RelativeFile(string repositoryRoot, string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return "(archivo desconocido)";
        return (Path.IsPathRooted(file) ? Path.GetRelativePath(repositoryRoot, file) : file).Replace('\\', '/');
    }

    private static string NormalizeWhitespace(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private sealed record EntryPoint(
        string Endpoint,
        string File,
        int Line,
        SyntaxNode? HandlerNode,
        string Kind,
        string? Contract = null,
        string? OwnerType = null,
        string Confidence = "declared");
}
