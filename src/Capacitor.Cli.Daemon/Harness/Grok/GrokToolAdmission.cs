using System.Text.Json;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Harness.Grok;

/// <summary>
/// Grok names an MCP call structurally: a <c>use_tool</c> call whose <c>rawInput</c> is
/// <c>{variant: "UseTool", tool_name: "&lt;server&gt;__&lt;tool&gt;"}</c>, with the same name as its title.
/// The admitted identities are the wire names of the servers this launch injects, so a frame is approved
/// only when it calls a tool on one of them; every other frame, an edit or a command included, is not.
/// </summary>
internal static class GrokToolAdmission {
    const string Separator = "__";

    internal static readonly UnattendedToolAdmitter Admitter = new(AdmittedFor, IsAdmitted);

    internal static IReadOnlySet<string> AdmittedFor(IReadOnlyList<AcpMcpServerSpec> injected, LaunchIdentity _) =>
        injected.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

    internal static bool IsAdmitted(JsonElement toolCall, IReadOnlySet<string> admitted) {
        if (admitted.Count == 0 || toolCall.ValueKind != JsonValueKind.Object) return false;
        if (toolCall.Obj("_meta")?.Obj("x.ai/tool")?.Str("name") != "use_tool") return false;
        if (toolCall.Obj("rawInput") is not { } raw || raw.Str("variant") != "UseTool" || raw.Str("tool_name") is not { } name) return false;
        if (toolCall.Str("title") != name) return false;

        var separator = name.IndexOf(Separator, StringComparison.Ordinal);

        return separator > 0 && separator + Separator.Length < name.Length && admitted.Contains(name[..separator]);
    }
}
