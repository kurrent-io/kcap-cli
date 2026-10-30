using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

sealed class SpyHostedAgentLauncher(string vendor, string cliPath) : IHostedAgentLauncher {
    public string Vendor             { get; }       = vendor;
    public string CliPath            { get; }       = cliPath;
    public bool   SupportsUnattended { get; init; } = true;
    public bool   SupportsResume     { get; init; }

    public int        PrepareCalls   { get; private set; }
    public int        BuildArgsCalls { get; private set; }
    public int        CleanupCalls   { get; private set; }
    public Exception? PrepareThrow   { get; init; }

    public LauncherContext? LastContext { get; private set; }

    /// <summary>The session a local launch reports running under, as a vendor that pins one does.</summary>
    public string? PassthroughSessionId { get; init; }

    public string? PassthroughPermissionMode { get; init; }

    /// <summary>Runs inside <see cref="Prepare"/>, after the launch wrote its pre-start ledger entry and
    /// before the PTY spawns.</summary>
    public Action<LauncherContext>? OnPrepare { get; set; }

    public bool IsAvailable() => true;

    public void Prepare(LauncherContext ctx) {
        PrepareCalls++;
        LastContext = ctx;
        OnPrepare?.Invoke(ctx);

        if (PrepareThrow is not null) throw PrepareThrow;
    }

    public LaunchArgs BuildArgs(LauncherContext ctx) {
        BuildArgsCalls++;

        return new LaunchArgs(Args: [], McpConfigPath: null);
    }

    public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs) {
        BuildArgsCalls++;

        return new LaunchArgs(Args: [.. userArgs], McpConfigPath: null, PassthroughSessionId, PassthroughPermissionMode);
    }

    public void Cleanup(AgentInstance agent) {
        CleanupCalls++;
    }
}
