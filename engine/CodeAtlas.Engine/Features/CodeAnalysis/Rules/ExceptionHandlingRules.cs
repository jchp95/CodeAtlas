using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>catch blocks that discard the exception without any observable handling.</summary>
sealed class SwallowedExceptionRule : ICodeRule
{
    public RuleDescriptor Descriptor { get; } = new("ATL1004", "SwallowedException", "reliability",
        FindingSeverity.Warning, "catch block that discards the exception without rethrowing, logging or using it.");

    public void Analyze(RuleContext context)
    {
        foreach (var clause in context.Root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (clause.Filter is not null) continue;

            var body = clause.Block;
            var exceptionType = clause.Declaration is null ? null : context.Model.GetTypeInfo(clause.Declaration.Type, context.Cancellation).Type;
            var broad = exceptionType is null || exceptionType.SpecialType == SpecialType.System_Object
                || exceptionType.IsNamed("System", "Exception") || exceptionType.IsNamed("System", "SystemException");
            var isEmpty = body.Statements.Count == 0;

            var rethrows = body.DescendantNodes().OfType<ThrowStatementSyntax>().Any() || body.DescendantNodes().OfType<ThrowExpressionSyntax>().Any();
            if (rethrows) continue;

            var variable = clause.Declaration?.Identifier.ValueText;
            var usesVariable = !string.IsNullOrEmpty(variable) && body.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.ValueText == variable);
            var invokesAnything = body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any();
            if (!isEmpty && (usesVariable || invokesAnything)) continue;
            if (!isEmpty && !broad) continue;

            var typeName = exceptionType?.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat) ?? "any exception";
            var validation = isEmpty ? ValidationLevel.StaticProven : ValidationLevel.StaticRisk;
            context.Report(clause,
                isEmpty ? $"Empty catch discards {typeName}" : $"catch ({typeName}) neither logs, uses nor rethrows the exception",
                isEmpty
                    ? "The catch block contains no statements, so the failure is silently dropped."
                    : "The block performs no call (no logging or compensation), does not reference the exception variable and does not rethrow.",
                validation, isEmpty ? "high" : "medium",
                new[]
                {
                    context.Evidence("catch", isEmpty ? "The block is empty." : "No throw, no method call and no reference to the exception variable inside the block.", clause),
                    new FindingEvidence("exception-type", broad ? $"The handler is broad ({typeName}); unrelated failures are also swallowed." : $"The handler is specific ({typeName}).")
                },
                "Failures in the try block become invisible: state may be left inconsistent and the cause of later misbehavior is lost.",
                "Handle it explicitly: log with context, translate to a domain result, or narrow the type to exactly what is expected and comment why it is safe to ignore. Rethrow anything unexpected.",
                "Force the try block to throw (fault injection or a test double) and assert that the failure is observable (log entry, result, or rethrow).",
                new[] { "An intentional best-effort ignore (for example cleanup on shutdown) is not distinguishable from an accident." },
                isEmpty && broad ? FindingSeverity.Warning : FindingSeverity.Info,
                discriminator: clause.ToString());
        }
    }
}

/// <summary>'throw ex;' inside a catch block resets the stack trace.</summary>
sealed class ThrowResetsStackRule : ICodeRule
{
    public RuleDescriptor Descriptor { get; } = new("ATL1005", "ThrowResetsStackTrace", "reliability",
        FindingSeverity.Warning, "'throw ex;' replaces the original stack trace; use 'throw;'.");

    public void Analyze(RuleContext context)
    {
        foreach (var statement in context.Root.DescendantNodes().OfType<ThrowStatementSyntax>())
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (statement.Expression is not IdentifierNameSyntax identifier) continue;
            if (context.Model.GetSymbolInfo(identifier, context.Cancellation).Symbol is not ILocalSymbol local) continue;
            if (local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.Cancellation) is not CatchDeclarationSyntax) continue;

            var fix = context.FixFor("Replace 'throw " + identifier.Identifier.ValueText + ";' with 'throw;'",
                new TextEdit(context.RelativeFile, statement.SpanStart, statement.Span.Length, "throw;"));
            context.Report(statement,
                $"'throw {identifier.Identifier.ValueText};' resets the stack trace",
                "Rethrowing the caught exception object restarts its stack trace at this statement, hiding where the failure originated.",
                ValidationLevel.StaticProven, "high",
                new[] { context.Evidence("throw", $"'{identifier.Identifier.ValueText}' is the catch variable, rethrown by value.", statement) },
                "Diagnosis of production failures points at the rethrow site instead of the origin.",
                "Use 'throw;' to preserve the stack trace, or wrap with 'throw new X(message, ex)' when adding context.",
                "Throw from a nested method inside the try block and compare ex.StackTrace before and after the rethrow.",
                Array.Empty<string>(), fix: fix);
        }
    }
}
