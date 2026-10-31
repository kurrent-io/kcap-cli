namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The held lifecycle lock, and the only way to change the registry or the journal.
///
/// <para>Setup from the CLI and from the desktop wizard, upgrade, uninstall, and
/// <c>kcap local start</c>, <c>stop</c> and <c>restart</c> all hold it. It is always taken before a
/// config lock, never after: config locks live under <c>KCAP_CONFIG_DIR</c> and cannot order anything
/// across config roots.</para>
///
/// <para>An exclusive open of <c>lifecycle.lock</c>, which is a <c>flock</c> on macOS and Linux. The
/// kernel drops it when the process dies, so a crash never leaves the lock held, and the file is
/// never deleted: unlinking it would let the next taker lock a fresh inode beside a holder of the
/// old one.</para>
/// </summary>
public sealed class LifecycleLease : IDisposable {
    static readonly TimeSpan PollGap = TimeSpan.FromMilliseconds(100);

    readonly FileStream _lock;
    readonly TimeProvider _time;

    LifecycleLease(LocalRoot root, FileStream @lock, TimeProvider time) {
        Root  = root;
        _lock = @lock;
        _time = time;
    }

    public LocalRoot Root { get; }

    /// <summary>Probes an existing lifecycle lock without creating or changing any files.</summary>
    public static bool IsHeld(LocalRoot root) {
        try {
            using var probe = new FileStream(root.LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        } catch (FileNotFoundException) {
            return false;
        } catch (DirectoryNotFoundException) {
            return false;
        } catch (IOException ex) when ((ex.HResult & 0xffff) == (OperatingSystem.IsWindows() ? 32 : OperatingSystem.IsMacOS() ? 35 : 11)) {
            return true;
        }
    }

    /// <summary>Waits up to <paramref name="wait"/> for the lock. Another holder past that is an error
    /// naming it, not a silent skip: the caller's operation did not run.</summary>
    public static async Task<LifecycleLease> AcquireAsync(LocalRoot root, TimeSpan wait, TimeProvider time, CancellationToken ct) {
        root.EnsureCreated();

        var deadline = time.GetUtcNow() + wait;
        var options  = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        while (true) {
            ct.ThrowIfCancellationRequested();

            try {
                return new(root, new FileStream(root.LockPath, options), time);
            } catch (IOException) when (time.GetUtcNow() < deadline) {
                await Task.Delay(PollGap, time, ct);
            } catch (IOException ex) {
                throw new LocalLifecycleException(
                    "Another kcap is changing the local installation. Wait for it to finish, then try again.", ex);
            }
        }
    }

    public StateRead<InstallRegistry> ReadRegistry() => LocalStateFiles.ReadRegistry(Root);

    /// <summary>Durable before it returns, so a caller may delete what the new registry no longer
    /// names.</summary>
    public void PublishRegistry(InstallRegistry registry) => LocalStateFiles.PublishRegistry(Root, registry);

    public StateRead<LifecycleJournalEntry> ReadJournal() => LocalStateFiles.ReadJournal(Root);

    /// <summary>Records a new operation, refusing to overwrite one that has not been recovered.</summary>
    public LifecycleJournalEntry Begin(string operation, string phase, ServerStart serverStart, string? installationId = null,
        IReadOnlyDictionary<string, string>? details = null) {
        if (ReadJournal() is not StateRead<LifecycleJournalEntry>.Absent)
            throw new InvalidOperationException("A lifecycle operation is already recorded; recover it before beginning another.");

        var entry = new LifecycleJournalEntry(
            operation, phase, serverStart, installationId, _time.GetUtcNow(), details ?? new Dictionary<string, string>());

        LocalStateFiles.WriteJournal(Root, entry);

        return entry;
    }

    /// <summary>Records that <paramref name="entry"/>'s operation reached a new phase.</summary>
    public LifecycleJournalEntry Advance(LifecycleJournalEntry entry, string phase, ServerStart serverStart,
        IReadOnlyDictionary<string, string>? details = null) {
        var next = entry with { Phase = phase, ServerStart = serverStart, Details = details ?? entry.Details };

        LocalStateFiles.WriteJournal(Root, next);

        return next;
    }

    /// <summary>Ends the operation: with no journal, nothing is left to finish or undo.</summary>
    public void Complete() => LocalStateFiles.ClearJournal(Root);

    public void Dispose() => _lock.Dispose();
}
