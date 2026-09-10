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
            if (!root.IsObject) return false;
            if (string.IsNullOrEmpty(root.Str("kind"))) return false;
            // Absent reads as v1; present must be the supported number, so a null or a string fails
            // rather than reading as the default the deserializer would hand back.
            if (root.Prop("contract_version") is not null && root.Num("contract_version") != SupportedContractVersion) return false;
            envelope = JsonSerializer.Deserialize(line, CapacitorJsonContext.Default.AcpEventEnvelope);
            return true;
        } catch (JsonException) {
            return false;
        }
    }
}
