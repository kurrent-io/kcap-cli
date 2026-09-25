namespace Capacitor.Cli.Capture;

public sealed record RedactionOutcome(string Line, RedactionLossReason? Loss,
    int InputUtf16Length, int InputUtf8Bytes);
