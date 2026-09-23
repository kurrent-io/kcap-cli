namespace Capacitor.Cli.Capture;

internal sealed record CaptureLossMarker {
    public string Type { get; } = "kcap_capture_loss";
    public int Version { get; } = 1;
    public required string Reason { get; init; }
    public required int InputUtf16Length { get; init; }
    public required int InputUtf8Bytes { get; init; }

    public static string ReasonName(RedactionLossReason reason) => reason switch {
        RedactionLossReason.InputLimit => "input_limit",
        RedactionLossReason.MalformedInput => "malformed_input",
        RedactionLossReason.RegexTimeout => "regex_timeout",
        RedactionLossReason.RecordBudget => "record_budget",
        RedactionLossReason.OutputLimit => "output_limit",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}
