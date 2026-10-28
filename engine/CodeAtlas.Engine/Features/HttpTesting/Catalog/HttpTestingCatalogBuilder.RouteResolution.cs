using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static partial class HttpTestingCatalogBuilder
{
    private sealed record StringConstantCandidate(string Name, string ContainingType, string QualifiedType,
        ExpressionSyntax Expression);

    private static Dictionary<string, string> BuildStringConstantIndex(string[] files)
    {
        var candidates = new List<StringConstantCandidate>();
        foreach (var file in files.Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var syntaxRoot = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Preview), file).GetRoot();
                foreach (var field in syntaxRoot.DescendantNodes().OfType<FieldDeclarationSyntax>().Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword)))
                {
                    if (!field.Declaration.Type.ToString().Equals("string", StringComparison.Ordinal)) continue;
                    var type = field.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
                    if (type is null) continue;
                    var namespaceName = string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(ns => ns.Name.ToString()));
                    var qualifiedType = string.IsNullOrEmpty(namespaceName) ? type.Identifier.ValueText : namespaceName + "." + type.Identifier.ValueText;
                    foreach (var variable in field.Declaration.Variables)
                        if (variable.Initializer is not null)
                            candidates.Add(new StringConstantCandidate(variable.Identifier.ValueText, type.Identifier.ValueText, qualifiedType, variable.Initializer.Value));
                }
            }
            catch { /* One unreadable source file must not prevent the rest of the catalog from being built. */ }
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var simpleCounts = candidates.GroupBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        for (var pass = 0; pass <= candidates.Count; pass++)
        {
            var changed = false;
            foreach (var candidate in candidates)
            {
                if (resolved.ContainsKey(candidate.QualifiedType + "." + candidate.Name)) continue;
                var value = ResolveStringExpression(candidate.Expression, resolved);
                if (value is null) continue;
                resolved[candidate.QualifiedType + "." + candidate.Name] = value;
                resolved[candidate.ContainingType + "." + candidate.Name] = value;
                if (simpleCounts[candidate.Name] == 1) resolved[candidate.Name] = value;
                changed = true;
            }
            if (!changed) break;
        }
        return resolved;
    }

    private static Dictionary<string, string> ResolveMapGroups(SyntaxNode syntaxRoot, IReadOnlyDictionary<string, string> constants,
        out HashSet<string> unresolvedGroups)
    {
        var definitions = syntaxRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Initializer?.Value is InvocationExpressionSyntax invocation && InvokedName(invocation.Expression) == "MapGroup")
            .Select(variable => (Name: variable.Identifier.ValueText, Invocation: (InvocationExpressionSyntax)variable.Initializer!.Value))
            .GroupBy(definition => definition.Name, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Invocation, StringComparer.Ordinal);
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        string? Resolve(string name)
        {
            if (resolved.TryGetValue(name, out var existing)) return existing;
            if (!definitions.TryGetValue(name, out var invocation) || !visiting.Add(name)) return null;
            try
            {
                var localPrefix = FirstString(invocation.ArgumentList, constants);
                if (string.IsNullOrWhiteSpace(localPrefix)) return null;
                var parentPrefix = string.Empty;
                if (invocation.Expression is MemberAccessExpressionSyntax member && member.Expression is IdentifierNameSyntax parentIdentifier
                    && definitions.ContainsKey(parentIdentifier.Identifier.ValueText))
                {
                    var parent = Resolve(parentIdentifier.Identifier.ValueText);
                    if (parent is null) return null;
                    parentPrefix = parent;
                }
                var full = CombineRoute(parentPrefix, localPrefix);
                resolved[name] = full;
                return full;
            }
            finally { visiting.Remove(name); }
        }

        foreach (var name in definitions.Keys) _ = Resolve(name);
        unresolvedGroups = definitions.Keys.Where(name => !resolved.ContainsKey(name)).ToHashSet(StringComparer.Ordinal);
        return resolved;
    }
}
