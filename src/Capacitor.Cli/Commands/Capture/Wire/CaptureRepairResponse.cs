using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands.Capture.Wire;

internal sealed record CaptureRepairResponse(
    [property: JsonPropertyName("repair_id")] string RepairId,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("scanned_sources")] int ScannedSources,
    [property: JsonPropertyName("matched_records")] int MatchedRecords,
    [property: JsonPropertyName("restored_records")] int RestoredRecords,
    [property: JsonPropertyName("gaps")] IReadOnlyList<CaptureRepairGap> Gaps,
    [property: JsonPropertyName("remaining_gap_count")] int RemainingGapCount,
    [property: JsonPropertyName("accounting_current")] bool AccountingCurrent) {
    [JsonPropertyName("candidate_records")]
    public int CandidateRecords { get; init; }
}
