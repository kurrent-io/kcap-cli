using System.Collections.Immutable;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>
/// The ordered list of tools a Pi reviewer is offered. The extension's manifest and the <c>--tools</c>
/// allowlist are both rendered from it: Pi drops an allowlisted name that matches nothing without a
/// diagnostic, and drops a registered tool the allowlist omits, so two lists would fail silently.
/// </summary>
internal static class PiReviewerToolSurface {
    internal static readonly ImmutableArray<string> FileTools = ["read_file", "list_directory", "search_files"];

    /// <summary>Pi activates a built-in exactly when the allowlist names it, and an extension tool
    /// replaces a built-in of the same name — so no entry may be, or collide with, one of these.</summary>
    internal static readonly ImmutableArray<string> PiBuiltInNames =
        ["read", "bash", "powershell", "edit", "write", "grep", "find", "ls"];

    /// <summary>Canonical output order — file tools, then the result channel, then every other
    /// server — is fixed here regardless of where the caller placed the result channel in
    /// <paramref name="servers"/>: a launcher assembling the server list has no reason to order it
    /// this way, so the surface enforces it rather than trusting caller order.</summary>
    internal static IReadOnlyList<PiReviewerTool> For(IReadOnlyList<AcpMcpServerSpec> servers) {
        var resultChannel = servers.FirstOrDefault(s => IsResultChannel(s.Name));

        if (resultChannel is null)
            throw new InvalidOperationException(
                "pi_reviewer_launch_context_incomplete: the launch carries no result channel.");

        var tools = new List<PiReviewerTool>();
        tools.AddRange(FileTools.Select(name => new PiReviewerTool(name, null, null)));
        tools.AddRange(KcapMcpRegistry.ReservedResultChannelTools
            .Where(t => t.UnattendedSafe)
            .Select(t => new PiReviewerTool(t.Name, resultChannel.Name, t.Name)));

        foreach (var server in servers) {
            if (IsResultChannel(server.Name)) continue;

            if (!KcapMcpRegistry.ReviewFlowUnattendedSafeTools.TryGetValue(server.Name, out var safe))
                throw new InvalidOperationException(
                    $"pi_reviewer_unclassified_server: '{server.Name}' has no unattended-safe tool classification.");

            var prefix = server.Name.Replace('-', '_');
            tools.AddRange(safe.OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => new PiReviewerTool($"{prefix}_{n}", server.Name, n)));
        }

        return tools;
    }

    internal static string AllowlistArg(IReadOnlyList<PiReviewerTool> tools) =>
        string.Join(",", tools.Select(t => t.PiName));

    static bool IsResultChannel(string name) =>
        string.Equals(name, KcapMcpRegistry.ReservedResultChannelId, StringComparison.OrdinalIgnoreCase);
}
