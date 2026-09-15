namespace Capacitor.Cli.Daemon.Services;

/// One fetched batch. Until <see cref="Publish"/> it is a staging directory that
/// <see cref="Dispose"/> deletes; after it, <see cref="Rollback"/> deletes the published directory,
/// so a delivery the agent never accepted leaves nothing behind.
internal sealed class AttachmentBatch(
        string stagingDirectory, string publishedDirectory, IReadOnlyList<string> paths) : IDisposable {
    bool _published;
    bool _gone;

    /// Trailer form: relative to the worktree for Worktree placement, absolute for DaemonStore.
    public IReadOnlyList<string> Paths => paths;

    public string Directory => publishedDirectory;

    /// The whole batch becomes visible in one rename: a reader never sees a half-written directory.
    internal void Publish() {
        System.IO.Directory.Move(stagingDirectory, publishedDirectory);
        _published = true;
    }

    public void Rollback() {
        if (_gone) return;

        _gone = true;
        var dir = _published ? publishedDirectory : stagingDirectory;

        // A rollback that cannot delete must not replace the failure that prompted it: what is left
        // behind is collected by the next fetch's sweep and by the store's startup sweep.
        try {
            if (System.IO.Directory.Exists(dir)) WorktreeManager.DeleteTreeNoFollow(dir);
        } catch { /* swept later */ }
    }

    public void Dispose() {
        if (!_published) Rollback();
    }
}
