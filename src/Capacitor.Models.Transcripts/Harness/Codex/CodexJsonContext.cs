using System.Text.Json.Serialization;

namespace Capacitor.Models.Transcripts.Harness.Codex;

/// Snake-case names and every property written, nulls included: the shape the server packs these
/// records into a Struct with.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CodexSessionExtension))]
[JsonSerializable(typeof(CodexAssistantTextExtension))]
[JsonSerializable(typeof(CodexAgentMessageExtension))]
[JsonSerializable(typeof(CodexToolCallExtension))]
[JsonSerializable(typeof(CodexExecResultExtension))]
[JsonSerializable(typeof(CodexPatchResultExtension))]
[JsonSerializable(typeof(CodexTurnTimingExtension))]
[JsonSerializable(typeof(CodexUsageBackfilledEvent))]
[JsonSerializable(typeof(ContextCompactedEvent))]
public sealed partial class CodexJsonContext : JsonSerializerContext;
