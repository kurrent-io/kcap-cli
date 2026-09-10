using System.Text.Json;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// A session's unresolved interrupts, folded from its event stream the way the server's own
/// seed does: issued minus resolved, cleared by a session end.
public sealed record InterruptReconciliation(IReadOnlyList<PendingInterrupt> Pending, bool Ended) {
    const string ElicitationToolName = "AskUserQuestion";

    public static InterruptReconciliation FromDetail(SessionDetailDto detail) {
        var pending = new Dictionary<string, PendingInterrupt>(StringComparer.Ordinal);
        var ended = detail.EndedAt is not null;
        foreach (var evt in detail.Events ?? []) {
            switch (evt.EventType) {
                case "InterruptIssued" when evt.Body is { } body && Read(body, "request_id", "requestId") is { Length: > 0 } id:
                    if (Parse(id, body) is { } interrupt) pending[id] = interrupt;
                    break;
                case "InterruptResolved" when evt.Body is { } body && Read(body, "request_id", "requestId") is { Length: > 0 } id:
                    pending.Remove(id);
                    break;
                case "SessionEnded":
                case var t when t.StartsWith("UserClosedSession", StringComparison.Ordinal):
                    pending.Clear();
                    ended = true;
                    break;
            }
        }
        if (ended) pending.Clear();
        return new([.. pending.Values], ended);
    }

    static PendingInterrupt? Parse(string id, JsonElement body) {
        var kind = Read(body, "kind", "kind") ?? "";
        var toolName = Read(body, "tool_name", "toolName") ?? "";
        var prompt = Read(body, "prompt", "prompt") ?? "";
        var timestamp = Read(body, "timestamp", "timestamp");
        var at = DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : DateTimeOffset.MinValue;
        var extensions = Elem(body, "extensions", "extensions");
        var acp = extensions is { } ext ? Elem(Elem(ext, "acp", "acp"), "interaction", "interaction") : null;
        var claude = extensions is { } ext2 ? Elem(ext2, "claude_code", "claudeCode") : null;

        if (kind == "permission") {
            if (acp is { } a) {
                return new(id, PendingInterruptKind.AcpPermission, Read(a, "tool_name", "toolName") ?? toolName,
                    Elem(a, "tool_input", "toolInput"), prompt, Options(a), false, null, null, at);
            }
            var block = claude is { } c ? Elem(c, "permission", "permission") : null;
            return new(id, PendingInterruptKind.ClaudePermission,
                toolName.Length > 0 ? toolName : block is { } b ? Read(b, "tool_name", "toolName") ?? "" : "",
                block is { } b2 ? Elem(b2, "tool_input", "toolInput") : null, prompt, [], false, null, null, at);
        }
        if (kind == "input" && toolName == ElicitationToolName) {
            if (acp is { } a) {
                var options = Options(a);
                var bounds = options.FirstOrDefault(o => o is { MinSelections: not null, MaxSelections: not null });
                return new(id, PendingInterruptKind.AcpQuestion, toolName, null, prompt, options,
                    Bool(a, "is_multi_select", "isMultiSelect") ?? false,
                    bounds?.MinSelections ?? Int(a, "min_selections", "minSelections"),
                    bounds?.MaxSelections ?? Int(a, "max_selections", "maxSelections"), at);
            }
            return new(id, PendingInterruptKind.TranscriptQuestion, toolName, null, prompt, [], false, null, null, at);
        }
        return null;
    }

    static IReadOnlyList<AcpInteractionOption> Options(JsonElement interaction) {
        if (Elem(interaction, "options", "options") is not { ValueKind: JsonValueKind.Array } arr) return [];
        var list = new List<AcpInteractionOption>();
        foreach (var o in arr.EnumerateArray()) {
            var optionId = Read(o, "option_id", "optionId");
            if (string.IsNullOrEmpty(optionId)) continue;
            list.Add(new AcpInteractionOption {
                OptionId = optionId, Label = Read(o, "label", "label") ?? optionId, Description = Read(o, "description", "description"),
                Kind = Read(o, "kind", "kind"), MinSelections = Int(o, "min_selections", "minSelections"), MaxSelections = Int(o, "max_selections", "maxSelections"),
            });
        }
        return list;
    }

    static JsonElement? Elem(JsonElement? obj, string snake, string camel) =>
        obj is { ValueKind: JsonValueKind.Object } o ? (o.Prop(snake) ?? o.Prop(camel)) is { ValueKind: not JsonValueKind.Null } e ? e : null : null;
    static string? Read(JsonElement obj, string snake, string camel) => Elem(obj, snake, camel) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;
    static bool? Bool(JsonElement obj, string snake, string camel) => Elem(obj, snake, camel) is { } e && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
    static int? Int(JsonElement obj, string snake, string camel) => Elem(obj, snake, camel) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var i) ? i : null;
}
