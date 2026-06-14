using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket entry points invoked by <see cref="LocalControlServer"/>. The bodies are
/// filled in across the local-attach milestones (attach loop, spawn path, agent list);
/// these stubs keep the listener compiling until then.
internal partial class AgentOrchestrator {
    public virtual Task HandleLocalSpawnAsync(LocalFrame spawn, Stream stream, CancellationToken ct) => Task.CompletedTask;
    public virtual Task HandleLocalAttachAsync(string agentId, Stream stream, CancellationToken ct) => Task.CompletedTask;
    public virtual Task HandleLocalListAsync(Stream stream, CancellationToken ct) => Task.CompletedTask;
}
