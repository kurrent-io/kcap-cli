namespace Capacitor.Cli.Commands;

/// <summary>One local note mapped onto a memory save. A non-null <see cref="SkipReason"/> means it will not be sent;
/// <see cref="Notes"/> name what the mapping changed on the way.</summary>
public sealed record MemoryImportCandidate(
    string                FileName,
    string?               Slug,
    string?               Description,
    string?               Kind,
    string?               Content,
    string?               SkipReason,
    IReadOnlyList<string> Notes
) {
    public static MemoryImportCandidate Skip(string fileName, string? slug, string reason) =>
        new(fileName, slug, null, null, null, reason, []);
}
