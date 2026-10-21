using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts;

/// The `openai` extension slug, where the schema keeps a model's opaque encrypted reasoning.
public static class OpenAiExtension {
    public const string Slug = "openai";

    public static Struct Thinking(string raw) {
        var thinking = new Struct();
        thinking.Fields["raw"] = Value.ForString(raw);
        var block = new Struct();
        block.Fields["thinking"] = Value.ForStruct(thinking);
        return block;
    }
}
