/// <summary>
/// How much of a claim has actually been established. A finding never moves up this ladder
/// without new evidence, and a suspicion must never be rendered as a demonstrated fact.
/// </summary>
enum ValidationLevel
{
    /// <summary>The pattern exists in the code; established by syntax/semantic analysis. Consequences may still be conditional.</summary>
    StaticProven,
    /// <summary>A potential problem inferred from code shape; needs confirmation before being treated as a defect.</summary>
    StaticRisk,
    /// <summary>Unconfirmed interpretation that needs a human or a runtime check.</summary>
    Hypothesis,
    /// <summary>Observed during a real execution (trace, log, debugger).</summary>
    RuntimeObserved,
    /// <summary>Quantified by a repeatable measurement (profile, benchmark, counter).</summary>
    Measured,
    /// <summary>Reproduced by a test or a scripted scenario.</summary>
    Reproduced,
    /// <summary>A fix exists but has not been verified.</summary>
    FixProposed,
    /// <summary>A fix was verified by tests or before/after measurements.</summary>
    FixValidated
}

static class ValidationLevelExtensions
{
    /// <summary>Evidence strength, independent of declaration order: a measurement outranks a static suspicion.</summary>
    public static int Rank(this ValidationLevel level) => level switch
    {
        ValidationLevel.Hypothesis => 0,
        ValidationLevel.StaticRisk => 1,
        ValidationLevel.StaticProven => 2,
        ValidationLevel.RuntimeObserved => 3,
        ValidationLevel.Measured => 4,
        ValidationLevel.Reproduced => 5,
        ValidationLevel.FixProposed => 6,
        ValidationLevel.FixValidated => 7,
        _ => 0
    };
}

enum FindingOrigin { Static, Dynamic, Test, Profile, External }

enum FindingSeverity { Info, Warning, Error }

enum FindingStatus { New, Confirmed, Fixed, Dismissed, Pending }

sealed record FindingLocation(string File, int Line, int Column, int EndLine, string? Symbol, string? Project);

sealed record FindingEvidence(string Kind, string Description, FindingLocation? Location = null, string? Snippet = null);

/// <summary>One hop of an execution path. <c>Resolution</c> is one of direct, virtual, interface, delegate, reflection, unknown.</summary>
sealed record CallPathStep(string Symbol, FindingLocation? Location, string Resolution);

/// <summary>A single replacement in the original text of <c>File</c>, expressed as UTF-16 offsets.</summary>
sealed record TextEdit(string File, int Offset, int Length, string NewText);

/// <summary>
/// An automatic correction that is only ever applied after its diff has been shown, and only when the
/// file still matches <c>BaseFileHash</c>, so edits the user made in the meantime are never overwritten.
/// </summary>
sealed record SuggestedFix(string Title, string File, string BaseFileHash, TextEdit[] Edits, string UnifiedDiff);

sealed record FindingHistoryEntry(string Timestamp, string Event, string? Detail = null);

/// <summary>
/// The common diagnostic contract shared by static analysis, architecture, debugging and profiling.
/// <c>Id</c> is derived from rule, project, file, enclosing symbol and the normalized offending code,
/// not from line numbers, so it survives edits that only move code.
/// </summary>
sealed record Finding(
    string Id,
    string RuleId,
    string Category,
    FindingSeverity Severity,
    ValidationLevel Validation,
    FindingOrigin Origin,
    string Title,
    string Description,
    FindingLocation Location,
    FindingEvidence[] Evidence,
    CallPathStep[]? CallPath,
    string Confidence,
    string[] Limitations,
    string Impact,
    string Recommendation,
    string Reproduction,
    SuggestedFix? Fix,
    FindingStatus Status = FindingStatus.New,
    string[]? RelatedFindingIds = null,
    FindingHistoryEntry[]? History = null);
