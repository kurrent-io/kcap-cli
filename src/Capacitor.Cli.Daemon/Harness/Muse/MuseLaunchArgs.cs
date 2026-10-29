using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>The <c>muse serve</c> argv for one launch. Sandbox posture is fixed per host, so the lane
/// decides it here; approval posture rides the wire.</summary>
internal static class MuseLaunchArgs {
    internal static IReadOnlyList<string> For(DaemonConfig config, RuntimeStartContext ctx, bool reviewer) {
        var argv = new List<string> { "serve", "--provider", MuseModelCatalog.LaunchProvider };

        if (ResolveModel(config, ctx) is { Length: > 0 } model) {
            argv.Add("--model");
            argv.Add(model);
        }

        // A trusted workspace's hooks and MCP servers run as the daemon user, so a reviewed repository
        // is never trusted. Proxy-only makes every new host an approval the user sees.
        argv.AddRange(reviewer
            ? ["--sandbox-network", "restricted", "--disable-write"]
            : ["--trust-workspace", "--sandbox-network", "proxy-only"]);

        return argv;
    }

    /// <summary>The launch's own model, else the daemon-wide default; <c>"default"</c> means the latter.</summary>
    internal static string? ResolveModel(DaemonConfig config, RuntimeStartContext ctx) =>
        !string.IsNullOrEmpty(ctx.Model) && !string.Equals(ctx.Model, "default", StringComparison.OrdinalIgnoreCase)
            ? ctx.Model
            : config.MuseModel;
}
