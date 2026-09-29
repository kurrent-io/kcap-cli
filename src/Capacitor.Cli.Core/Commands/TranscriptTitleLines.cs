using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.Harness.Gemini;
using Capacitor.Models.Transcripts.Harness.OpenCode;
using Capacitor.Models.Transcripts.Harness.Pi;

namespace Capacitor.Cli.Core.Commands;

/// <summary>Whether a transcript line carries the harness's own title, for vendors whose title lives inline in the
/// transcript rather than in a separate store the watcher polls, and which servers record it. Each vendor's reader
/// recognizes only what the server's own extractors record.</summary>
internal static class TranscriptTitleLines {
    /// <param name="sessionId">The watched session: a Claude title line copied in from another session (a resume carries
    /// its history) is not recorded for this one.</param>
    public static TranscriptTitleLineKind Classify(string vendor, string sessionId, string line) => vendor switch {
        "claude" => ClaudeTitleLine.CarriesTitle(line, sessionId, out var isRename)
            ? isRename ? TranscriptTitleLineKind.RecordedWithHarnessTitles : TranscriptTitleLineKind.RecordedByEveryServer
            : TranscriptTitleLineKind.None,
        "pi"       => Newer(PiTitleLine.CarriesTitle(line)),
        "gemini"   => Newer(GeminiTitleLine.CarriesTitle(line)),
        "opencode" => Newer(OpenCodeTitleLine.CarriesTitle(line)),
        _          => TranscriptTitleLineKind.None,
    };

    /// <summary>Vendors whose transcript can carry a title only a server with harness titles records.</summary>
    public static bool MayNeedHarnessTitles(string vendor) => vendor is "claude" or "pi" or "gemini" or "opencode";

    static TranscriptTitleLineKind Newer(bool carries) =>
        carries ? TranscriptTitleLineKind.RecordedWithHarnessTitles : TranscriptTitleLineKind.None;
}
