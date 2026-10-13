namespace Capacitor.Cli.Capture;

/// <summary>
/// The encoded form of the first <see cref="Consumed"/> raw lines; capture stopped before the next
/// one when fewer than all were consumed.
/// </summary>
internal sealed record CapturedLines(List<string> Lines, int Consumed);
