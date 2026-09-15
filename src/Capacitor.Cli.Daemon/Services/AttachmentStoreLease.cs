namespace Capacitor.Cli.Daemon.Services;

/// Scoped ownership of an agent's store directory across a launch: disposed without Keep() it
/// removes the directory, so every failure exit between the fetch and registration cleans up.
internal sealed class AttachmentStoreLease(AttachmentStore store, string agentId) : IDisposable {
    bool _keep;
    public void Keep() => _keep = true;
    public void Dispose() { if (!_keep) store.Remove(agentId); }
}
