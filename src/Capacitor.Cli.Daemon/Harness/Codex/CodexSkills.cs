using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>Maps a Codex app-server <c>skills/list</c> result to the picker's command list. Codex has
/// no slash-command surface of its own — skills are the closest analogue — so this is the one place
/// that shape is read, and it is deliberately defensive: the wire shape is not yet probe-confirmed
/// here, so an unexpected or absent shape yields an empty list rather than throwing, and the picker
/// simply shows nothing for Codex until the shape is verified.</summary>
internal static class CodexSkills {
    public static IReadOnlyList<HostedAgentCommand> Extract(JsonElement result) {
        var list = new List<HostedAgentCommand>();

        // Accept either a flat array (data / skills) of {name, description}, or a hooks/list-style
        // grouping whose entries carry a nested `skills` array. The first array that yields entries
        // wins, so a skill is never counted twice.
        foreach (var arrayName in new[] { "data", "skills" }) {
            if (result.Arr(arrayName) is not { } arr) continue;

            foreach (var entry in arr.EnumerateArray()) {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                if (entry.Arr("skills") is { } nested) {
                    foreach (var skill in nested.EnumerateArray()) Add(list, skill);
                } else {
                    Add(list, entry);
                }
            }

            if (list.Count > 0) break;
        }

        return list;
    }

    static void Add(List<HostedAgentCommand> list, JsonElement entry) {
        if (entry.ValueKind != JsonValueKind.Object) return;

        var name = entry.Str("name");
        if (string.IsNullOrWhiteSpace(name)) return;

        var description = entry.Str("description");
        list.Add(new HostedAgentCommand(
            name,
            string.IsNullOrWhiteSpace(description) ? null : description,
            ArgumentHint: null));
    }
}
