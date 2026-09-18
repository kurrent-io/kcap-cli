using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>Extracts the harness's slash commands from an ACP <c>availableCommands</c> array — the
/// same shape whether it rides an <c>available_commands_update</c>'s <c>update</c> object or the
/// <c>session/new</c> result. Each entry is <c>{ name, description }</c>; ACP carries no argument
/// hint, so that stays null.</summary>
internal static class AcpAvailableCommands {
    public static IReadOnlyList<HostedAgentCommand> Extract(JsonElement? source) {
        if (source is not { ValueKind: JsonValueKind.Object } obj) return [];
        if (obj.Arr("availableCommands") is not { } commands) return [];

        var list = new List<HostedAgentCommand>();

        foreach (var entry in commands.EnumerateArray()) {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var name = entry.Str("name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var description = entry.Str("description");
            list.Add(new HostedAgentCommand(
                name,
                string.IsNullOrWhiteSpace(description) ? null : description,
                ArgumentHint: null));
        }

        return list;
    }
}
