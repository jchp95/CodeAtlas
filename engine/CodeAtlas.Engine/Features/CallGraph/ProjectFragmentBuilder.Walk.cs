using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

sealed partial class ProjectFragmentBuilder
{
    private readonly record struct Flags(bool InLoop, bool InTry);

    /// <summary>Visits the operation tree of one owner in source order. Lambdas and local functions become owners of their own bodies.</summary>
    private void Walk(GraphNode owner, IOperation root, SyntaxTree tree)
    {
        var stack = new Stack<(IOperation Operation, Flags Flags)>();
        stack.Push((root, default));
        while (stack.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var (operation, flags) = stack.Pop();
            var children = new List<(IOperation, Flags)>();

            switch (operation)
            {
                case IAnonymousFunctionOperation lambda:
                {
                    var node = EnsureAnonymousNode(lambda, owner);
                    AddEdge(owner, node.SymbolId, EdgeResolution.Lambda, ToLocation(lambda.Syntax.GetLocation()), flags.InLoop, flags.InTry, false, false, false, null,
                        "lambda created here; whoever receives the delegate decides if and when it runs", EdgeCertainty.Potential);
                    Walk(node, lambda.Body, tree);
                    continue;
                }
                case ILocalFunctionOperation local:
                {
                    var node = EnsureNode(local.Symbol);
                    if (local.Body is { } body) Walk(node, body, tree);
                    continue;
                }
                case IInvocationOperation invocation:
                    VisitInvocation(owner, invocation, flags);
                    break;
                case IObjectCreationOperation creation when creation.Constructor is { } constructor:
                    AddResolved(owner, constructor, EdgeResolution.ObjectCreation, creation, flags, false, null);
                    break;
                case IPropertyReferenceOperation property:
                    VisitProperty(owner, property, flags);
                    break;
                case IMethodReferenceOperation reference:
                    AddResolved(owner, reference.Method, EdgeResolution.Delegate, reference, flags, false, null, EdgeCertainty.Potential);
                    break;
                case IInvalidOperation invalid when invalid.Syntax is InvocationExpressionSyntax syntax:
                    AddEdgeUnresolved(owner, syntax, flags);
                    break;
                case IReturnOperation { ReturnedValue: { } returned } when returned.Unwrap() is IObjectCreationOperation { Type: { } createdType }
                                                                         && createdType.ImplementsDisposable() && !createdType.IsTaskLike():
                    disposableCreators.Add(owner.Id);
                    break;
            }

            switch (operation)
            {
                case IForEachLoopOperation forEach:
                    foreach (var child in forEach.ChildOperations)
                        children.Add((child, child == forEach.Collection ? flags : flags with { InLoop = true }));
                    break;
                case IForLoopOperation forLoop:
                    foreach (var child in forLoop.ChildOperations)
                        children.Add((child, forLoop.Before.Contains(child) ? flags : flags with { InLoop = true }));
                    break;
                case ILoopOperation loop:
                    foreach (var child in loop.ChildOperations) children.Add((child, flags with { InLoop = true }));
                    break;
                case ITryOperation attempt:
                    foreach (var child in attempt.ChildOperations)
                        children.Add((child, child == attempt.Body && attempt.Catches.Length > 0 ? flags with { InTry = true } : flags));
                    break;
                default:
                    foreach (var child in operation.ChildOperations) children.Add((child, flags));
                    break;
            }

            for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    private void VisitInvocation(GraphNode owner, IInvocationOperation invocation, Flags flags)
    {
        var target = invocation.TargetMethod;
        var location = ToLocation(invocation.Syntax.GetLocation());

        if (target.MethodKind == MethodKind.DelegateInvoke)
        {
            AddEdge(owner, null, EdgeResolution.Delegate, location, flags.InLoop, flags.InTry, false, false, false, null,
                "delegate invocation: the target is decided at run time and is not statically known", EdgeCertainty.Unknown,
                label: invocation.Syntax.ToString().Split('(')[0].Trim());
            return;
        }

        var passesToken = invocation.Arguments.Any(a => a.ArgumentKind != ArgumentKind.DefaultValue && a.Value.Type.IsCancellationToken());
        var resolution = EdgeResolution.Direct;
        string? receiverType = null;
        if (IsExtensionCall(invocation)) resolution = EdgeResolution.ExtensionMethod;
        else if (invocation.IsVirtual && IsDispatchable(target, invocation))
        {
            resolution = target.ContainingType.TypeKind == TypeKind.Interface ? EdgeResolution.InterfaceDispatch : EdgeResolution.VirtualDispatch;
            receiverType = invocation.Instance?.Type is { } type ? identity.TypeId(type) : null;
        }
        AddResolved(owner, target, resolution, invocation, flags, passesToken, receiverType);
    }

    private static bool IsExtensionCall(IInvocationOperation invocation) =>
        invocation.TargetMethod.IsExtensionMethod && invocation.Instance is null && invocation.Syntax is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax access
        } && invocation.SemanticModel?.GetSymbolInfo(access.Expression).Symbol is not INamedTypeSymbol;

    private static bool IsDispatchable(IMethodSymbol target, IInvocationOperation invocation)
    {
        if (target.ContainingType.SpecialType == SpecialType.System_Object) return false;
        if (target.ContainingType.TypeKind != TypeKind.Interface && !(target.IsVirtual || target.IsAbstract || target.IsOverride)) return false;
        return !(target.IsSealed || target.ContainingType.IsSealed || invocation.Instance?.Type?.IsSealed == true);
    }

    private void VisitProperty(GraphNode owner, IPropertyReferenceOperation reference, Flags flags)
    {
        var property = reference.Property;
        if (!identity.IsSource(property) && !ExternalApiClassifier.IsBlockingWait(property.GetMethod ?? property.SetMethod!)) return; // external property reads are noise unless they block
        var isWrite = reference.Parent is IAssignmentOperation assignment && assignment.Target == reference;
        var isCompound = reference.Parent is ICompoundAssignmentOperation or IIncrementOrDecrementOperation;
        if ((isWrite || isCompound) && property.SetMethod is { } setter) AddAccessor(owner, setter, reference, flags);
        if ((!isWrite || isCompound) && property.GetMethod is { } getter) AddAccessor(owner, getter, reference, flags);
    }

    private void AddAccessor(GraphNode owner, IMethodSymbol accessor, IPropertyReferenceOperation reference, Flags flags)
    {
        var dispatch = (accessor.IsVirtual || accessor.IsAbstract || accessor.IsOverride || accessor.ContainingType.TypeKind == TypeKind.Interface)
            && !(accessor.IsSealed || accessor.ContainingType.IsSealed || reference.Instance?.Type?.IsSealed == true)
            && accessor.ContainingType.SpecialType != SpecialType.System_Object;
        if (!dispatch) { AddResolved(owner, accessor, EdgeResolution.PropertyAccess, reference, flags, false, null); return; }
        var resolution = accessor.ContainingType.TypeKind == TypeKind.Interface ? EdgeResolution.InterfaceDispatch : EdgeResolution.VirtualDispatch;
        AddResolved(owner, accessor, resolution, reference, flags, false, reference.Instance?.Type is { } type ? identity.TypeId(type) : null);
    }

    private void AddResolved(GraphNode owner, IMethodSymbol target, EdgeResolution resolution, IOperation operation, Flags flags,
        bool passesToken, string? receiverTypeId, EdgeCertainty certainty = EdgeCertainty.Proven)
    {
        var canonical = (IMethodSymbol)GraphIdentity.Canonical(target);
        var targetId = SymbolIdOf(canonical);
        if (!identity.IsSource(canonical)) EnsureNode(canonical); // external nodes are defined by the project that references them
        else if (identity.ProjectOf(canonical) == projectKey) EnsureNode(canonical);

        var dispatchKind = resolution is EdgeResolution.InterfaceDispatch or EdgeResolution.VirtualDispatch;
        var openWorld = dispatchKind && (!identity.IsSource(canonical) || ExternallyVisible(canonical));
        AddEdge(owner, targetId, resolution, ToLocation(operation.Syntax.GetLocation()), flags.InLoop, flags.InTry, passesToken,
            AcceptsToken(canonical), openWorld, receiverTypeId, null, certainty);
    }

    private void AddEdgeUnresolved(GraphNode owner, InvocationExpressionSyntax syntax, Flags flags)
    {
        var label = syntax.Expression.ToString();
        AddEdge(owner, null, EdgeResolution.Unresolved, ToLocation(syntax.GetLocation()), flags.InLoop, flags.InTry, false, false, false, null,
            "Roslyn could not bind this call (missing reference or compile error); its target is unknown", EdgeCertainty.Unknown, label: label);
    }

    private void AddEdge(GraphNode owner, string? targetSymbolId, EdgeResolution resolution, GraphLocation site, bool inLoop, bool inTry, bool passesToken,
        bool acceptsToken, bool openWorld, string? receiverTypeId, string? reason, EdgeCertainty certainty = EdgeCertainty.Proven, string? label = null)
    {
        var siteKey = $"{owner.Id}|{targetSymbolId ?? label}|{resolution}";
        var ordinal = siteOrdinals.GetValueOrDefault(siteKey);
        siteOrdinals[siteKey] = ordinal + 1;
        var siteId = "S-" + GraphIdentity.StableHash($"{siteKey}|{ordinal}");
        edges.Add(new RawEdge(owner.Id, targetSymbolId, resolution, certainty, siteId, site, inLoop, inTry, passesToken, acceptsToken, openWorld,
            receiverTypeId, label, reason));
    }

    private bool AcceptsToken(IMethodSymbol target)
    {
        var key = SymbolIdOf(target);
        if (acceptsTokenCache.TryGetValue(key, out var cached)) return cached;
        var result = target.Parameters.Any(p => p.Type.IsCancellationToken())
            || (target.ReducedFrom ?? target).ContainingType.GetMembers(target.Name).OfType<IMethodSymbol>()
                .Any(m => m.Parameters.Length > target.Parameters.Length && m.Parameters.Any(p => p.Type.IsCancellationToken()));
        acceptsTokenCache[key] = result;
        return result;
    }

    private static bool ExternallyVisible(ISymbol symbol)
    {
        for (ISymbol? current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)) return false;
        return true;
    }
}
