namespace Capacitor.Cli.Core.Commands;

public enum TranscriptTitleLineKind {
    None,

    /// <summary>A title every server records from the transcript: Claude's <c>ai-title</c>.</summary>
    RecordedByEveryServer,

    /// <summary>A title only a server with <c>/hooks/harness-title</c> records from the transcript.</summary>
    RecordedWithHarnessTitles,
}
