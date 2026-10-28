using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>A finding plus the facts used to give it a stable id once every rule has run.</summary>
sealed record FindingDraft(Finding Finding, string Fingerprint, int Position);

sealed class RuleContext
{
    private readonly ConcurrentBag<FindingDraft> sink;
    private readonly RuleDescriptor descriptor;
    private readonly RuleSettings settings;
    private readonly string? text;

    public RuleContext(string project, string relativeFile, SyntaxTree tree, SemanticModel model, RuleDescriptor descriptor,
        RuleSettings settings, ConcurrentBag<FindingDraft> sink, CancellationToken cancellation)
    {
        Project = project;
        RelativeFile = relativeFile;
        Tree = tree;
        Model = model;
        this.descriptor = descriptor;
        this.settings = settings;
        this.sink = sink;
        Cancellation = cancellation;
        text = tree.TryGetText(out var sourceText) ? sourceText.ToString() : null;
    }

    public string Project { get; }
    public string RelativeFile { get; }
    public SyntaxTree Tree { get; }
    public SemanticModel Model { get; }
    public CancellationToken Cancellation { get; }
    public int? Threshold => settings.Threshold;

    public CompilationUnitSyntax Root => (CompilationUnitSyntax)Tree.GetRoot(Cancellation);

    public FindingLocation LocationOf(SyntaxNode node)
    {
        var span = Tree.GetLineSpan(node.Span);
        return new FindingLocation(RelativeFile, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, EnclosingSymbolName(node), Project);
    }

    public FindingLocation LocationOf(SyntaxToken token)
    {
        var span = Tree.GetLineSpan(token.Span);
        return new FindingLocation(RelativeFile, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, EnclosingSymbolName(token.Parent), Project);
    }

    public string? EnclosingSymbolName(SyntaxNode? node)
    {
        if (node is null) return null;
        // A declaration anchors on the member it declares, not on the type that contains it.
        ISymbol? symbol = node is MemberDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or CatchClauseSyntax
            ? Model.GetDeclaredSymbol(node, Cancellation) : null;
        symbol ??= Model.GetEnclosingSymbol(node.SpanStart, Cancellation);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction }) symbol = symbol.ContainingSymbol;
        return symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
    }

    public string Snippet(SyntaxNode node)
    {
        var line = node.ToString().Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    public FindingEvidence Evidence(string kind, string description, SyntaxNode node) =>
        new(kind, description, LocationOf(node), Snippet(node));

    public SuggestedFix? FixFor(string title, params TextEdit[] edits)
    {
        if (text is null || edits.Length == 0) return null;
        return FixBuilder.Build(title, RelativeFile, text, edits);
    }

    public void Report(SyntaxNode anchor, string title, string description, ValidationLevel validation, string confidence,
        FindingEvidence[] evidence, string impact, string recommendation, string reproduction, string[] limitations,
        FindingSeverity? severity = null, CallPathStep[]? callPath = null, SuggestedFix? fix = null, string? discriminator = null)
    {
        if (!settings.Enabled) return;
        var location = LocationOf(anchor);
        var finding = new Finding(
            "", descriptor.Id, descriptor.Category, settings.Severity ?? severity ?? descriptor.DefaultSeverity, validation,
            FindingOrigin.Static, title, description, location, evidence, callPath, confidence, limitations, impact,
            recommendation, reproduction, fix, FindingStatus.New, null,
            new[] { new FindingHistoryEntry(DateTimeOffset.UtcNow.ToString("o"), "detected", $"{descriptor.Id} by CodeAtlas static analysis") });
        var fingerprint = FindingIdentity.Normalize(discriminator ?? anchor.ToString());
        sink.Add(new FindingDraft(finding, fingerprint, anchor.SpanStart));
    }
}

interface ICodeRule
{
    RuleDescriptor Descriptor { get; }
    void Analyze(RuleContext context);
}
