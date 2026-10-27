using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static partial class BusinessFlowAnalyzer
{
private sealed class FlowTrace(
        string repositoryRoot,
        IReadOnlyDictionary<SyntaxTree, SemanticModel> semanticModels,
        Dictionary<string, List<INamedTypeSymbol>> registrations,
        IReadOnlyDictionary<string, string[]> repositoryTypeIndex)
    {
        private readonly HashSet<string> visitedMethods = new(StringComparer.Ordinal);
        private readonly HashSet<string> visitedSourceMethods = new(StringComparer.Ordinal);
        private readonly HashSet<string> stepKeys = new(StringComparer.Ordinal);
        public List<BusinessFlowStep> Steps { get; } = [];

        public void AddStep(string kind, string symbol, string file, int line, string confidence, string detail)
        {
            if (Steps.Count >= MaxSteps) return;
            var key = $"{kind}|{symbol}|{file}|{line}";
            if (!stepKeys.Add(key)) return;
            Steps.Add(new BusinessFlowStep(Steps.Count, kind, symbol, file, Math.Max(1, line), confidence, detail));
        }

        public void TraceNode(SyntaxNode node, int depth)
        {
            if (depth > MaxDepth || Steps.Count >= MaxSteps) return;
            var eligibleInvocations = node.DescendantNodesAndSelf(child =>
                    child == node || child is not (MethodDeclarationSyntax or LocalFunctionStatementSyntax or LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
                .OfType<InvocationExpressionSyntax>()
                .OrderBy(invocation => invocation.SpanStart)
                .ToArray();

            foreach (var invocation in eligibleInvocations)
            {
                if (Steps.Count >= MaxSteps) break;
                TraceInvocation(invocation, depth);
            }
        }

        private void TraceInvocation(InvocationExpressionSyntax invocation, int depth)
        {
            var methodName = InvokedName(invocation.Expression);
            var semanticModel = semanticModels[invocation.SyntaxTree];
            var method = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            var callFile = RelativeFile(repositoryRoot, invocation.SyntaxTree.FilePath);
            var callLine = Line(invocation);

            if ((methodName is "Send" or "SendAsync") && TryFindRequestType(invocation, semanticModel, out var requestType))
            {
                var handlerResolution = FindRequestHandler(requestType, semanticModel.Compilation);
                if (handlerResolution is not null)
                {
                    var handler = handlerResolution.Method;
                    var declaration = FindMethodDeclaration(handler);
                    var file = declaration is null ? callFile : RelativeFile(repositoryRoot, declaration.SyntaxTree.FilePath);
                    var line = declaration is null ? callLine : Line(declaration);
                    var resolved = declaration is not null && handlerResolution.MatchedIRequestHandler;
                    var mapping = handlerResolution.MatchedIRequestHandler ? "IRequestHandler<TRequest>" : "convención RequestNameHandler";
                    AddStep("request-handler", $"{handler.ContainingType?.Name ?? "Handler"}.{handler.Name}", file, line,
                        resolved ? "resolved" : "candidate",
                        $"Despacho Send(...) asociado al request {requestType.Name} mediante {mapping}.");
                    TraceMethod(handler, depth + 1);
                }
                else
                {
                    AddStep("request-dispatch", $"{requestType.Name} · {methodName}", callFile, callLine, "candidate",
                        "Se detectó el envío de un request, pero no se encontró un handler local verificable.");
                }
                return;
            }

            if (IsMessagePublish(method, methodName))
            {
                var contract = FindMessageContract(invocation, semanticModel, repositoryTypeIndex) ?? "Contrato no resuelto";
                AddStep("message-publish", contract, callFile, callLine,
                    method is not null && method.ContainingNamespace.ToDisplayString().StartsWith("MassTransit", StringComparison.Ordinal) ? "resolved" : "candidate",
                    $"Publicación de mensaje detectada mediante {methodName}; el transporte, el routing y la entrega deben verificarse por configuración o telemetría.");
                return;
            }

            // An incomplete project reference set can leave Roslyn unable to bind a source-to-source
            // member call even when both declarations are present in the scanned project. Preserve the
            // source trace by resolving an unambiguous receiver-type/method pair from syntax in that case.
            if ((method is null || FindMethodDeclaration(method) is null)
                && TryTraceSourceLocalMethod(invocation, semanticModel, methodName, depth))
                return;

            if (method is null) return;

            if (method.ContainingType?.TypeKind == TypeKind.Interface)
            {
                var implementation = FindImplementation(method.ContainingType);
                var implementationMethod = implementation is null ? null : FindImplementationMethod(implementation, method);
                if (implementationMethod is not null && HasExecutableBody(implementationMethod))
                {
                    var declaration = FindMethodDeclaration(implementationMethod);
                    if (declaration is not null)
                    {
                        AddStep("service-implementation", $"{implementation!.Name}.{implementationMethod.Name}",
                            RelativeFile(repositoryRoot, declaration.SyntaxTree.FilePath), Line(declaration), "resolved",
                            $"Implementación inferida a partir del registro DI de {method.ContainingType.Name}.");
                        TraceMethod(implementationMethod, depth + 1);
                        return;
                    }
                }

                // Interfaces are useful architecture boundaries even where the implementation cannot be resolved.
                if (LooksLikeDependencyBoundary(method.ContainingType.Name))
                {
                    AddStep("dependency-call", $"{method.ContainingType.Name}.{method.Name}", callFile, callLine, "candidate",
                        "Llamada a una abstracción local; no se pudo resolver una implementación concreta mediante el registro DI.");
                    return;
                }
            }

            var targetDeclaration = FindMethodDeclaration(method);
            if (targetDeclaration is not null && HasExecutableBody(method))
            {
                var typeName = method.ContainingType?.Name ?? "Método local";
                AddStep("method-call", $"{typeName}.{method.Name}", RelativeFile(repositoryRoot, targetDeclaration.SyntaxTree.FilePath), Line(targetDeclaration), "resolved",
                    "Llamada interna resuelta mediante el símbolo de método de Roslyn.");
                TraceMethod(method, depth + 1);
                return;
            }

            var containingTypeName = method.ContainingType?.Name ?? string.Empty;
            if (LooksLikeDependencyBoundary(containingTypeName) || LooksLikePersistence(method, containingTypeName))
            {
                var kind = LooksLikePersistence(method, containingTypeName) ? "persistence-call"
                    : containingTypeName.Contains("Client", StringComparison.OrdinalIgnoreCase) ? "client-call" : "dependency-call";
                AddStep(kind, $"{containingTypeName}.{method.Name}", callFile, callLine,
                    method.ContainingType?.Locations.Any(location => location.IsInSource) == true ? "candidate" : "inferred",
                    "Llamada a dependencia externa o infraestructura; no se ha ejecutado ni verificado el efecto en runtime.");
            }
        }

        private void TraceMethod(IMethodSymbol method, int depth)
        {
            if (depth > MaxDepth || Steps.Count >= MaxSteps) return;
            var key = MethodKey(method);
            if (!visitedMethods.Add(key)) return;
            var declaration = FindMethodDeclaration(method);
            if (declaration is null) return;
            SyntaxNode? body = (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody?.Expression;
            if (body is not null) TraceNode(body, depth);
        }

        private bool TryTraceSourceLocalMethod(
            InvocationExpressionSyntax invocation,
            SemanticModel semanticModel,
            string methodName,
            int depth)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                return false;

            var receiverType = GetReceiverType(memberAccess.Expression, semanticModel);
            var receiverTypeName = receiverType?.Name;
            var receiverFullName = receiverType is not null && receiverType.TypeKind != TypeKind.Error
                ? TypeKey(receiverType)
                : null;

            // Roslyn may not produce a usable type symbol for a primary-constructor parameter when
            // unrelated framework interfaces in the same fixture/project are unresolved. In that
            // case, recover only the declared parameter type from its owning class; do not guess from
            // the invoked method name alone.
            if (string.IsNullOrWhiteSpace(receiverTypeName)
                && memberAccess.Expression is IdentifierNameSyntax receiverIdentifier)
            {
                var ownerClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                var primaryParameter = ownerClass?.ParameterList?.Parameters.FirstOrDefault(parameter =>
                    parameter.Identifier.ValueText == receiverIdentifier.Identifier.ValueText);
                var declaredType = primaryParameter?.Type?.ToString();
                if (!string.IsNullOrWhiteSpace(declaredType))
                {
                    receiverTypeName = declaredType!.Split('.').Last();
                    if (declaredType!.Contains(".", StringComparison.Ordinal))
                        receiverFullName = declaredType.Replace("global::", string.Empty, StringComparison.Ordinal);
                }
            }

            if (string.IsNullOrWhiteSpace(receiverTypeName))
                return false;

            var candidates = semanticModels.Keys
                .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                .Where(type => string.Equals(type.Identifier.ValueText, receiverTypeName, StringComparison.Ordinal))
                .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>()
                    .Where(candidate => candidate.Identifier.ValueText == methodName
                        && candidate.ParameterList.Parameters.Count == invocation.ArgumentList.Arguments.Count)
                    .Select(candidate => (Type: type, Method: candidate)))
                .Where(candidate =>
                {
                    if (string.IsNullOrWhiteSpace(receiverFullName)) return true;
                    var namespaceName = string.Join(".", candidate.Type.Ancestors()
                        .OfType<BaseNamespaceDeclarationSyntax>()
                        .Select(namespaceNode => namespaceNode.Name.ToString()).Reverse());
                    var candidateFullName = string.IsNullOrWhiteSpace(namespaceName)
                        ? candidate.Type.Identifier.ValueText
                        : $"{namespaceName}.{candidate.Type.Identifier.ValueText}";
                    return string.Equals(candidateFullName, receiverFullName, StringComparison.Ordinal);
                })
                .DistinctBy(candidate => (candidate.Method.SyntaxTree.FilePath, candidate.Method.SpanStart))
                .Take(2)
                .ToArray();

            // Ambiguous matches are deliberately ignored rather than fabricating a call edge.
            if (candidates.Length != 1)
                return false;

            var match = candidates[0];
            var relativeFile = RelativeFile(repositoryRoot, match.Method.SyntaxTree.FilePath);
            var symbol = $"{match.Type.Identifier.ValueText}.{match.Method.Identifier.ValueText}";
            AddStep("method-call", symbol, relativeFile, Line(match.Method), "inferred",
                "Llamada interna resuelta por sintaxis porque Roslyn no pudo enlazar un símbolo de método único; revisar referencias del proyecto para confirmar el enlace semántico.");

            var visitKey = $"{match.Method.SyntaxTree.FilePath}|{match.Method.SpanStart}|{match.Method.Identifier.ValueText}";
            if (visitedSourceMethods.Add(visitKey))
            {
                SyntaxNode? body = (SyntaxNode?)match.Method.Body ?? match.Method.ExpressionBody?.Expression;
                if (body is not null)
                    TraceNode(body, depth + 1);
            }

            return true;
        }

        private static ITypeSymbol? GetReceiverType(ExpressionSyntax receiver, SemanticModel semanticModel)
        {
            var type = semanticModel.GetTypeInfo(receiver).Type;
            if (type is not null && type.TypeKind != TypeKind.Error)
                return type;

            var symbol = semanticModel.GetSymbolInfo(receiver).Symbol;
            var symbolType = symbol switch
            {
                IParameterSymbol parameter => parameter.Type,
                ILocalSymbol local => local.Type,
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                IEventSymbol eventSymbol => eventSymbol.Type,
                _ => null
            };
            return symbolType is not null && symbolType.TypeKind != TypeKind.Error ? symbolType : null;
        }

        private INamedTypeSymbol? FindImplementation(INamedTypeSymbol interfaceType)
        {
            var keys = new[] { TypeKey(interfaceType), interfaceType.Name };
            foreach (var key in keys)
            {
                if (!registrations.TryGetValue(key, out var choices)) continue;
                if (choices.Count == 1) return choices[0];
            }
            return null;
        }

        private static IMethodSymbol? FindImplementationMethod(INamedTypeSymbol implementation, IMethodSymbol contractMethod)
        {
            return implementation.GetMembers(contractMethod.Name).OfType<IMethodSymbol>()
                .Where(candidate => !candidate.IsAbstract && candidate.Parameters.Length == contractMethod.Parameters.Length)
                .OrderByDescending(candidate => candidate.Locations.Any(location => location.IsInSource))
                .FirstOrDefault();
        }

        private static HandlerResolution? FindRequestHandler(ITypeSymbol requestType, Compilation compilation)
        {
            var requestName = requestType.Name;
            foreach (var tree in compilation.SyntaxTrees)
            {
                foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                    var symbol = semanticModel.GetDeclaredSymbol(declaration);
                    if (symbol is null) continue;
                    var interfaceMatch = symbol.AllInterfaces.Any(iface => iface.Name == "IRequestHandler"
                        && iface.TypeArguments.Length > 0
                        && SymbolEqualityComparer.Default.Equals(iface.TypeArguments[0], requestType));
                    var conventionMatch = string.Equals(declaration.Identifier.ValueText, requestName + "Handler", StringComparison.Ordinal);
                    if (!interfaceMatch && !conventionMatch) continue;
                    var handle = symbol.GetMembers().OfType<IMethodSymbol>()
                        .Where(candidate => candidate.Name is "Handle" or "HandleAsync")
                        .FirstOrDefault(candidate => FindMethodDeclaration(candidate) is not null);
                    if (handle is not null) return new HandlerResolution(handle, interfaceMatch);
                    var syntaxHandle = declaration.Members.OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault(candidate => candidate.Identifier.ValueText is "Handle" or "HandleAsync");
                    if (syntaxHandle is not null && semanticModel.GetDeclaredSymbol(syntaxHandle) is IMethodSymbol syntaxSymbol)
                        return new HandlerResolution(syntaxSymbol, interfaceMatch);
                }
            }
            return null;
        }

        private sealed record HandlerResolution(IMethodSymbol Method, bool MatchedIRequestHandler);

        private static bool TryFindRequestType(InvocationExpressionSyntax invocation, SemanticModel semanticModel, out ITypeSymbol requestType)
        {
            foreach (var creation in invocation.ArgumentList.Arguments
                         .SelectMany(argument => argument.Expression.DescendantNodesAndSelf())
                         .OfType<ObjectCreationExpressionSyntax>())
            {
                var type = semanticModel.GetTypeInfo(creation).Type ?? semanticModel.GetSymbolInfo(creation.Type).Symbol as ITypeSymbol;
                if (type is null || type.TypeKind == TypeKind.Error) continue;
                requestType = type;
                return true;
            }
            requestType = null!;
            return false;
        }
    }
}
