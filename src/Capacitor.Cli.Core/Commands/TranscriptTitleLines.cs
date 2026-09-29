using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.Harness.Gemini;
using Capacitor.Models.Transcripts.Harness.OpenCode;
using Capacitor.Models.Transcripts.Harness.Pi;

namespace Capacitor.Cli.Core.Commands;

/// <summary>Whether a transcript line carries the harness's own title, for vendors whose title lives inline in the
/// transcript rather than in a separate store the watcher polls. Each vendor's reader recognizes only what the
/// server's own extractors record.</summary>
internal static class TranscriptTitleLines {
    public static bool CarriesHarnessTitle(string vendor, string line) => vendor switch {
        "claude"   => ClaudeTitleLine.CarriesTitle(line),
        "pi"       => PiTitleLine.CarriesTitle(line),
        "gemini"   => GeminiTitleLine.CarriesTitle(line),
        "opencode" => OpenCodeTitleLine.CarriesTitle(line),
        _          => false,
    };
}
