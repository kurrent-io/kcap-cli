using System.Text.Json.Nodes;

namespace Capacitor.Cli.Harness.GrokBot;

/// <summary>Per-Bot delivery state, one JSON file under the config root. Written whole through a
/// temporary file, so a crash mid-write leaves the previous state rather than a torn one.</summary>
public sealed class GrokBotStateStore(string path) {
    public Dictionary<string, GrokBotBotState> Load() {
        var result = new Dictionary<string, GrokBotBotState>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;

        JsonObject? bots;
        try {
            bots = JsonNode.Parse(File.ReadAllText(path))?["bots"] as JsonObject;
        } catch (System.Text.Json.JsonException) {
            return result;
        }

        if (bots is null) return result;

        foreach (var (agentId, node) in bots) {
            if (node is not JsonObject bot || bot["lastSeq"]?.GetValue<int>() is not { } lastSeq) continue;

            GrokBotOpenSession? open = null;
            if (bot["open"] is JsonObject o
             && o["sessionId"]?.GetValue<string>() is { } sessionId
             && o["firstEntryId"]?.GetValue<string>() is { } firstEntryId
             && o["startedAtMs"]?.GetValue<long>() is { } startedAtMs
             && o["lastEntryMs"]?.GetValue<long>() is { } lastEntryMs)
                open = new GrokBotOpenSession(sessionId, firstEntryId, startedAtMs, lastEntryMs);

            result[agentId] = new GrokBotBotState(lastSeq, open);
        }

        return result;
    }

    public void Save(IReadOnlyDictionary<string, GrokBotBotState> states) {
        var bots = new JsonObject();

        foreach (var (agentId, state) in states) {
            var bot = new JsonObject { ["lastSeq"] = state.LastSeq };
            if (state.Open is { } open)
                bot["open"] = new JsonObject {
                    ["sessionId"]    = open.SessionId,
                    ["firstEntryId"] = open.FirstEntryId,
                    ["startedAtMs"]  = open.StartedAtMs,
                    ["lastEntryMs"]  = open.LastEntryMs
                };
            bots[agentId] = bot;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, new JsonObject { ["bots"] = bots }.ToJsonString());
        File.Move(temp, path, overwrite: true);
    }
}
