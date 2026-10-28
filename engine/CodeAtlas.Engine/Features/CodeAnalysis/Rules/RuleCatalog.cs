static class RuleCatalog
{
    /// <summary>Bumped whenever a rule's behavior changes so cached results are invalidated.</summary>
    public const string Version = "1.0.0";

    public static IReadOnlyList<ICodeRule> All { get; } = new ICodeRule[]
    {
        new SyntaxErrorRule(),
        new BlockingOnAsyncRule(),
        new AsyncVoidRule(),
        new CancellationPropagationRule(),
        new SwallowedExceptionRule(),
        new ThrowResetsStackRule(),
        new UndisposedResourceRule(),
        new RemoteCallInLoopRule(),
        new MultipleEnumerationRule(),
        new CyclomaticComplexityRule(),
        new MethodLengthRule()
    };
}
