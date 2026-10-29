using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>Maps a Pi dialog <c>extension_ui_request</c> onto the daemon's elicitation route and the
/// answer back onto the <c>extension_ui_response</c> Pi blocks on.</summary>
internal static class PiDialogBridge {
    const string ConfirmYes = "yes";
    const string ConfirmNo  = "no";

    /// <summary>Bounds the routed prompt, as the ACP elicitation path does; an editor's prefill is the
    /// only part that can grow without limit.</summary>
    internal const int MaxPromptLength = 8 * 1024;

    /// <summary>The server refuses a card with more options than this.</summary>
    internal const int MaxOptions = 32;

    /// <summary>The methods Pi blocks on until a host answers.</summary>
    public static bool IsDialog(string? method) => method is "select" or "confirm" or "input" or "editor";

    /// <summary>Null when the request cannot be asked (a select with nothing to pick, or more than the
    /// server takes); the caller cancels it so Pi does not wait forever.</summary>
    public static AcpInteractionRequest? ToInteraction(string agentId, string sessionId, JsonElement request) {
        var method = request.Str("method");
        var title  = request.Str("title") ?? "";

        AcpInteractionOption[]? options = null;
        string prompt;

        switch (method) {
            case "select":
                if (SelectOptions(request) is not { Length: > 0 and <= MaxOptions } labels) return null;
                options = [.. labels.Select((label, i) => new AcpInteractionOption(i.ToString(CultureInfo.InvariantCulture), label, null))];
                prompt  = title;
                break;

            case "confirm":
                options = [new(ConfirmYes, "Yes", null), new(ConfirmNo, "No", null)];
                prompt  = Join(title, request.Str("message"));
                break;

            case "input":
                prompt = title;
                break;

            case "editor":
                prompt = request.Str("prefill") is { Length: > 0 } prefill ? Join(title, "Current text:\n" + prefill) : title;
                break;

            default:
                return null;
        }

        if (string.IsNullOrWhiteSpace(prompt)) prompt = "Pi is asking for input.";
        if (prompt.Length > MaxPromptLength) prompt = prompt[..MaxPromptLength];

        return new AcpInteractionRequest(
            AgentId:       agentId,
            AcpSessionId:  sessionId,
            Kind:          "elicitation",
            ToolName:      null,
            ToolInput:     null,
            ToolCallId:    null,
            Prompt:        prompt,
            Options:       options,
            IsMultiSelect: false);
    }

    /// <summary>The <c>extension_ui_response</c> line for <paramref name="decision"/>; a decision that
    /// answers nothing cancels, which Pi reads as its own dismissal default.</summary>
    public static string ToResponse(string requestId, AcpInteractionDecision decision, JsonElement request) {
        if (decision.Outcome != "answered") return PiRpc.ExtensionUiCancelled(requestId);

        switch (request.Str("method")) {
            case "confirm":
                return decision.SelectedOptionId switch {
                    ConfirmYes => PiRpc.ExtensionUiConfirmed(requestId, true),
                    ConfirmNo  => PiRpc.ExtensionUiConfirmed(requestId, false),
                    _          => PiRpc.ExtensionUiCancelled(requestId),
                };

            case "select":
                // A typed answer goes through as the value: a select's result is a string either way,
                // and dropping what the viewer wrote would be worse than letting the extension judge it.
                if (decision.SelectedOptionId is { } id
                 && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                 && SelectOptions(request) is { } labels && index < labels.Length)
                    return PiRpc.ExtensionUiValue(requestId, labels[index]);
                break;
        }

        return string.IsNullOrWhiteSpace(decision.FreeText)
            ? PiRpc.ExtensionUiCancelled(requestId)
            : PiRpc.ExtensionUiValue(requestId, decision.FreeText);
    }

    static string[]? SelectOptions(JsonElement request) =>
        request.Arr("options") is { } arr ? [.. arr.EnumerateArray().Where(o => o.IsString).Select(o => o.GetString()!)] : null;

    static string Join(string first, string? second) =>
        string.IsNullOrWhiteSpace(second) ? first
      : string.IsNullOrWhiteSpace(first)  ? second
      : first + "\n\n" + second;
}
