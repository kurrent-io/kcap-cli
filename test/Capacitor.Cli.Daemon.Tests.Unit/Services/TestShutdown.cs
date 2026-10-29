using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

static class TestShutdown {
    public static DaemonShutdownIpc Ipc(AgentOrchestrator orchestrator, RestartCoordinator restart, IHostApplicationLifetime? lifetime = null) =>
        new(orchestrator, restart, lifetime ?? new AgentOrchestratorHarness.StubHostLifetime(), NullLogger<DaemonShutdownIpc>.Instance);
}
