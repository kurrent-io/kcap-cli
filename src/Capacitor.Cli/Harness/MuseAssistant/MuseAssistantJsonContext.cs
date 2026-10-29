using System.Text.Json.Serialization;

namespace Capacitor.Cli.Harness.MuseAssistant;

[JsonSourceGenerationOptions(WriteIndented = true, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(MuseAssistantChatState))]
[JsonSerializable(typeof(MuseAssistantScopeFile))]
[JsonSerializable(typeof(MuseAssistantManifest))]
internal sealed partial class MuseAssistantJsonContext : JsonSerializerContext;
