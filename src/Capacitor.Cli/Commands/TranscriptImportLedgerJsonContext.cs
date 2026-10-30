using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

[JsonSerializable(typeof(Dictionary<string, Dictionary<string, TranscriptImportMark>>))]
internal partial class TranscriptImportLedgerJsonContext : JsonSerializerContext;
