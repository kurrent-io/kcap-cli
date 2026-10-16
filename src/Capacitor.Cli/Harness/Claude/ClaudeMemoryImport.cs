using System.Text;
using System.Text.RegularExpressions;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>Maps Claude Code auto-memory notes onto memory saves, within the limits the server enforces.</summary>
public static partial class ClaudeMemoryImport {
    public const int DescriptionMaxChars = 300;
    public const int ContentMaxUtf8Bytes = 65_536;

    /// <summary>Maps every note in order. A slug already taken earlier in the same batch is skipped, since the server
    /// would answer it as a conflict with the note just sent.</summary>
    public static IReadOnlyList<MemoryImportCandidate> Plan(IEnumerable<ClaudeMemoryFile> files) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<MemoryImportCandidate>();

        foreach (var file in files) {
            var candidate = Map(file);
            if (candidate.SkipReason is null && !seen.Add(candidate.Slug!))
                candidate = MemoryImportCandidate.Skip(file.FileName, candidate.Slug, "same slug as an earlier file");
            plan.Add(candidate);
        }

        return plan;
    }

    public static MemoryImportCandidate Map(ClaudeMemoryFile file) {
        var notes = new List<string>();

        var slug = MemorySlug.Normalize(file.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(file.FileName));
        if (slug is null) return MemoryImportCandidate.Skip(file.FileName, null, "name has no usable slug characters");

        // Blank lines framing the body are dropped; indentation and anything inside it are markdown and kept.
        var content = file.Body.TrimStart('\r', '\n').TrimEnd();
        if (content.Length == 0) return MemoryImportCandidate.Skip(file.FileName, slug, "empty body");
        if (Encoding.UTF8.GetByteCount(content) > ContentMaxUtf8Bytes)
            return MemoryImportCandidate.Skip(file.FileName, slug, $"body over {ContentMaxUtf8Bytes / 1024} KiB");

        var description = Whitespace().Replace(file.Description ?? "", " ").Trim();
        if (description.Length == 0) return MemoryImportCandidate.Skip(file.FileName, slug, "no description");
        if (description.Length > DescriptionMaxChars) {
            var cut = char.IsHighSurrogate(description[DescriptionMaxChars - 1]) ? DescriptionMaxChars - 1 : DescriptionMaxChars;
            description = description[..cut];
            notes.Add($"description cut to {DescriptionMaxChars} chars");
        }

        var type = file.Type?.Trim().ToLowerInvariant();
        var kind = type switch {
            "user"                                 => "preference",
            "feedback" or "project" or "reference" => type,
            _                                      => null
        };
        if (kind is null) {
            notes.Add(file.Type is { Length: > 0 } t ? $"unknown type '{t}', saved as reference" : "no type, saved as reference");
            kind = "reference";
        }

        return new(file.FileName, slug, description, kind, content, null, notes);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
