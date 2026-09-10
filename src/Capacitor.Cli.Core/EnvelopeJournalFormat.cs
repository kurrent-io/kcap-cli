using System.Text.Json;

namespace Capacitor.Cli.Core;

/// One envelope per JSONL line, the same bytes the server receives. Validity is decided here, not by
/// the deserializer: a record struct decodes from `{}` or a null `kind` without complaint, and a
/// later contract version may reuse a known kind with different fields.
public static class EnvelopeJournalFormat {
    public const int SupportedContractVersion = 1;

    public static string Write(AcpEventEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, CapacitorJsonContext.Default.AcpEventEnvelope);

    public static bool TryRead(string line, out AcpEventEnvelope envelope) {
        envelope = default;
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(kind.GetString())) return false;
            if (root.TryGetProperty("contract_version", out var version)
             && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != SupportedContractVersion)) return false;
            envelope = JsonSerializer.Deserialize(line, CapacitorJsonContext.Default.AcpEventEnvelope);
            return true;
        } catch (JsonException) {
            return false;
        }
    }
}
