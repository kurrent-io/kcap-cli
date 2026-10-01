using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A fence handler over the orchestrator's own fence, for tests that start a control server.</summary>
static class TestFences {
    public static AdmissionFenceIpc Ipc(DaemonConfig config, AgentOrchestrator orchestrator, EvalContextCache? evalCache = null) =>
        new(config, orchestrator.Admission, orchestrator, evalCache ?? new EvalContextCache(TimeProvider.System), NullLogger<AdmissionFenceIpc>.Instance);
}
