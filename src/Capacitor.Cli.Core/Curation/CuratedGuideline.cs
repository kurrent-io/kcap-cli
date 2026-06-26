namespace Capacitor.Cli.Core.Curation;

/// <summary>One promoted guideline destined for an instruction file.</summary>
public sealed record CuratedGuideline(string Category, string Text);

/// <summary>Raised when an instruction file's managed markers are malformed; apply fails closed.</summary>
public sealed class CuratedBlockException : Exception {
    public CuratedBlockException(string message) : base(message) { }
}

/// <summary>What apply will do to one instruction file.</summary>
public enum CurateAction { Create, Update, Remove, NoOp }

public sealed record FilePlan(
    string                Path,
    CurateAction          Action,
    string?               NewContent,   // null only on NoOp; Create/Update/Remove carry the new file content
    IReadOnlyList<string> Added,        // bullet texts added vs. the current block
    IReadOnlyList<string> Removed       // bullet texts removed vs. the current block
);

public sealed record ApplyPlan(IReadOnlyList<FilePlan> Files);
