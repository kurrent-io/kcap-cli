using System.Text.Json.Nodes;
using System.Text.Json;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>Builders for the MSP requests the daemon sends, each returning the serialized line.</summary>
internal static class MuseMsp {
    /// <summary>A UUIDv7, the shape MSP wants for command and session ids.</summary>
    public static string NewId() => Guid.CreateVersion7().ToString("D");

    public static string Initialize(long id, string version) =>
        Request(id, "initialize", new JsonObject {
            ["clientInfo"]   = new JsonObject { ["name"] = "kcap_daemon", ["version"] = version },
            ["capabilities"] = new JsonObject { ["requestedCapabilities"] = new JsonArray(JsonValue.Create("sessionMcp")) },
        });

    public static string Initialized() =>
        new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized" }.ToJsonString();

    public static string SessionStart(
        long        id,
        string      commandId,
        string      sessionId,
        string      workspaceRoot,
        string?     modelId,
        string      providerId,
        string      permissionProfile,
        string      approvalMode,
        JsonObject? mcpServers
    ) {
        var p = new JsonObject {
            ["commandId"]         = commandId,
            ["sessionId"]         = sessionId,
            ["workspaceRoot"]     = workspaceRoot,
            ["providerId"]        = providerId,
            ["permissionProfile"] = permissionProfile,
            ["approvalMode"]      = approvalMode,
        };

        if (modelId is not null) p["modelId"] = modelId;
        if (mcpServers is not null) p["config"] = new JsonObject { ["mcpServers"] = mcpServers };

        return Request(id, "session/start", p);
    }

    public static string SessionResume(long id, string commandId, string sessionId) =>
        Request(id, "session/resume", Command(commandId, sessionId));

    public static string TurnStart(long id, string commandId, string sessionId, string text) {
        var p = Command(commandId, sessionId);
        p["input"] = TextInput(text);
        return Request(id, "turn/start", p);
    }

    public static string TurnSteer(long id, string commandId, string sessionId, string expectedTurnId, string text) {
        var p = Command(commandId, sessionId);
        p["expectedTurnId"] = expectedTurnId;
        p["input"]          = TextInput(text);
        return Request(id, "turn/steer", p);
    }

    public static string TurnCancel(long id, string commandId, string sessionId, string turnId) {
        var p = Command(commandId, sessionId);
        p["turnId"] = turnId;
        return Request(id, "turn/cancel", p);
    }

    /// <summary><paramref name="requirementId"/> is echoed exactly as the approval request carried it;
    /// it can be an object.</summary>
    public static string ApprovalDecide(
        long        id,
        string      commandId,
        string      sessionId,
        string      approvalId,
        JsonElement requirementId,
        string      choiceId,
        string?     feedback
    ) {
        var p = Command(commandId, sessionId);
        p["approvalId"]    = approvalId;
        p["requirementId"] = JsonNode.Parse(requirementId.GetRawText());
        p["choiceId"]      = choiceId;
        if (feedback is not null) p["feedback"] = feedback;
        return Request(id, "approval/decide", p);
    }

    public static string UserInputAnswer(
        long                             id,
        string                           commandId,
        string                           sessionId,
        string                           userInputId,
        IReadOnlyList<MuseUserInputAnswer> answers
    ) {
        var items = new JsonArray(answers.Select(a => (JsonNode)AnswerItem(a)).ToArray());

        var p = Command(commandId, sessionId);
        p["userInputId"] = userInputId;
        p["answers"]     = items;
        return Request(id, "userInput/answer", p);
    }

    static JsonObject AnswerItem(MuseUserInputAnswer a) {
        var item = new JsonObject { ["questionId"] = a.QuestionId };

        if (a.SelectedLabels is { Count: > 0 } labels)
            item["selectedLabels"] = new JsonArray(labels.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());
        else if (a.SelectedLabel is not null)
            item["selectedLabel"] = a.SelectedLabel;
        else if (a.FreeText is not null)
            item["freeText"] = a.FreeText;

        return item;
    }

    public static string UserInputCancel(long id, string commandId, string sessionId, string userInputId) {
        var p = Command(commandId, sessionId);
        p["userInputId"] = userInputId;
        return Request(id, "userInput/cancel", p);
    }

    /// <summary>The empty presentation receipt a server-initiated <c>approval/request</c> or
    /// <c>userInput/request</c> must be answered with; the decision itself travels as a command.</summary>
    public static string RequestReceipt(long id) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject() }.ToJsonString();

    static JsonObject Command(string commandId, string sessionId) =>
        new() { ["commandId"] = commandId, ["sessionId"] = sessionId };

    static JsonArray TextInput(string text) =>
        new(new JsonObject { ["type"] = "text", ["text"] = text });

    static string Request(long id, string method, JsonObject p) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p }.ToJsonString();
}
