using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>kcap's own MCP servers for an interactive ACP launch's <c>session/new</c>, on the same kcap
/// command the review-flow result channel runs. Canonical names on purpose: a vendor that injects them
/// runs each in place of its own same-named registration, which an alias would sit beside instead.</summary>
internal static class AcpInteractiveKcapMcp {
    internal static IReadOnlyList<AcpMcpServerSpec> Build(RuntimeStartContext ctx, string vendor) {
        AcpMcpServerEnvVar[] env = string.IsNullOrEmpty(ctx.ServerUrl) ? [] : [new(ProfileOverrides.UrlVar, ctx.ServerUrl)];

        return [.. KcapMcpServers.ForHarness(vendor).Select(s => new AcpMcpServerSpec(s.Name, ctx.CapacitorPath, s.Args, env))];
    }
}
