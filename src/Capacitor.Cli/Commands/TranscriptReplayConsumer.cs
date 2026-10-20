namespace Capacitor.Cli.Commands;

/// <summary>One consumer of <see cref="TranscriptReplay"/>.</summary>
/// <param name="WindowOnly">Only the last <see cref="TranscriptReplay.WindowLines"/> lines before the cursor.</param>
/// <param name="OnLine">Takes one line; true once it wants no more.</param>
internal sealed record TranscriptReplayConsumer(bool WindowOnly, Func<string, bool> OnLine);
