using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Codex;

/// The `codex` extension slug. The session, phase and tool-call records are the slug's own fields;
/// the others nest under the field named here.
public static class CodexExtension {
    public const string Slug         = "codex";
    public const string AgentMessage = "agent_message";
    public const string Exec         = "exec";
    public const string Patch        = "patch";
    public const string Task         = "task";

    public static Struct Pack<T>(T record, JsonTypeInfo<T> info) => Struct.Parser.ParseJson(JsonSerializer.Serialize(record, info));

    public static Struct Nest<T>(string field, T record, JsonTypeInfo<T> info) {
        var block = new Struct();
        block.Fields[field] = Value.ForStruct(Pack(record, info));
        return block;
    }

    /// Shallow-merges the block's fields over the slug's, creating the slug when absent.
    public static void Merge(MapField<string, Struct> extensions, Struct block) {
        if (extensions.TryGetValue(Slug, out var existing)) {
            foreach (var (field, value) in block.Fields) existing.Fields[field] = value;
        } else {
            extensions[Slug] = block;
        }
    }
}
