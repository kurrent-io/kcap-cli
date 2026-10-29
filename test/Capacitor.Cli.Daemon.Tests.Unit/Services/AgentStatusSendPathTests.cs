using System.Runtime.CompilerServices;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentStatusSendPathTests {
    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException($"Could not locate repo root walking up from {here}");
    }

    /// <summary>Pins that agent status reaches the server through one method. A send added anywhere else
    /// would skip the verdict gate, and nothing at runtime would notice.</summary>
    [Test]
    public async Task AgentStatusChangedAsync_is_called_only_from_TrySendAgentStatus() {
        var path   = Path.Combine(RepoRoot(), "src", "Capacitor.Cli.Daemon", "Services", "AgentOrchestrator.cs");
        var source = await File.ReadAllTextAsync(path);

        var calls = source.Split('\n').Count(l => l.Contains("_server.AgentStatusChangedAsync(", StringComparison.Ordinal));

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(source).Contains("internal bool TrySendAgentStatus(");
    }
}
