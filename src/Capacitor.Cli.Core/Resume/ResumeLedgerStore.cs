using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Resume;

/// <summary>
/// Durable per-agent launch records under a daemon's <c>resume</c> directory. The file stem is a hash
/// of the agent id: the id crosses the wire unconstrained and must not reach a path. An unparseable
/// entry is renamed <c>.corrupt</c> and never acted on.
/// </summary>
public sealed class ResumeLedgerStore(string resumeDirectory, TimeProvider? time = null) {
    public const string NewerSchemaReason = "ledger_newer_than_daemon";

    const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode OwnerOnlyFile      = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static ResumeLedgerStore For(DaemonStore store, string daemonName, TimeProvider? time = null) =>
        new(store.ResumeDirectory(daemonName), time);

    /// <summary>Flow participants' entries, in a subdirectory a daemon that cannot resume them never reads.</summary>
    public static ResumeLedgerStore ParticipantsFor(DaemonStore store, string daemonName, TimeProvider? time = null) =>
        new(ParticipantsDirectory(store.ResumeDirectory(daemonName)), time);

    static string ParticipantsDirectory(string resumeDirectory) => Path.Combine(resumeDirectory, "participants");

    public static string FileStem(string agentId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant();

    public string EntryPath(string agentId) => Path.Combine(resumeDirectory, FileStem(agentId) + ".json");

    public string RingPath(string agentId) => Path.Combine(resumeDirectory, FileStem(agentId) + ".ring");

    public void Write(ResumeLedgerEntry entry) {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(resumeDirectory);
        else Directory.CreateDirectory(resumeDirectory, OwnerOnlyDirectory);

        var finalPath = EntryPath(entry.AgentId);
        var tempPath  = finalPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        var options   = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;

        // The successor's only record of the agent must survive a power loss.
        using (var fs = new FileStream(tempPath, options)) {
            JsonSerializer.Serialize(fs, entry, ResumeLedgerJsonContext.Default.ResumeLedgerEntry);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tempPath, finalPath, overwrite: true);
    }

    static readonly TimeSpan DeleteLockWait = TimeSpan.FromSeconds(1);

    /// <summary>Removes the entry under its <see cref="ResumeEntryLock"/>, so a locked update cannot write it
    /// back. A holder that keeps the lock past the wait does not stop the delete. The lock file stays: a
    /// holder locks the file it opened, so unlinking it would let the next opener lock a new one beside it.</summary>
    public bool Delete(string agentId) {
        var entryPath = EntryPath(agentId);
        var existed   = false;

        using (ResumeEntryLock.TryAcquire(entryPath, DeleteLockWait)) {
            foreach (var path in new[] { entryPath, RingPath(agentId) }) {
                if (!File.Exists(path)) continue;

                File.Delete(path);
                existed = true;
            }
        }

        return existed;
    }

    public ResumeLedgerEntry? TryRead(string agentId) => ReadFile(EntryPath(agentId));

    /// <summary>Reads, changes and writes the entry under its <see cref="ResumeEntryLock"/>. A null from
    /// <paramref name="change"/> leaves the entry as it is.</summary>
    public LockedUpdate TryUpdateUnderLock(string agentId, Func<ResumeLedgerEntry, ResumeLedgerEntry?> change, TimeSpan wait) {
        var path = EntryPath(agentId);
        if (!File.Exists(path)) return LockedUpdate.NoEntry;

        using var held = ResumeEntryLock.TryAcquire(path, wait);
        if (held is null) return LockedUpdate.LockTimeout;

        if (ReadFile(path) is not { } entry) return LockedUpdate.NoEntry;
        if (change(entry) is not { } changed) return LockedUpdate.Unchanged;

        Write(changed);

        return LockedUpdate.Written;
    }

    public IReadOnlyList<ResumeLedgerEntry> ReadAll() {
        var results = new List<ResumeLedgerEntry>();
        if (!Directory.Exists(resumeDirectory)) return results;

        foreach (var path in Directory.EnumerateFiles(resumeDirectory, "*.json")) {
            if (ReadFile(path) is { } entry) results.Add(entry);
        }

        return results;
    }

    public int MarkInterrupted(string reason, Func<ResumeLedgerEntry, bool>? where = null) {
        var changed = 0;

        foreach (var entry in ReadAll()) {
            // A newer-schema entry rewritten by this version would lose the fields this version does not know.
            if (entry.State == ResumeState.Interrupted || entry.SchemaVersion > ResumeLedgerEntry.CurrentSchemaVersion) continue;
            if (where is not null && !where(entry)) continue;

            Write(entry with { State = ResumeState.Interrupted, LastError = reason, UpdatedAt = (time ?? TimeProvider.System).GetUtcNow() });
            changed++;
        }

        return changed;
    }

    public static ResumeLedgerEntry? FindByAgentId(DaemonStore store, string agentId) =>
        LocateByAgentId(store, agentId)?.TryRead(agentId);

    /// <summary>The store, among every daemon's, that holds an entry for <paramref name="agentId"/>.</summary>
    public static ResumeLedgerStore? LocateByAgentId(DaemonStore store, string agentId) {
        if (!Directory.Exists(store.Directory)) return null;

        foreach (var daemonDirectory in Directory.EnumerateDirectories(store.Directory)) {
            var resume = Path.Combine(daemonDirectory, "resume");

            foreach (var candidate in new[] { new ResumeLedgerStore(resume), new ResumeLedgerStore(ParticipantsDirectory(resume)) }) {
                try {
                    if (candidate.TryRead(agentId) is not null) return candidate;
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // One unreadable daemon directory must not hide an entry in the next.
                }
            }
        }

        return null;
    }

    /// <summary>The agent whose entry runs <paramref name="sessionId"/>, by its kcap or vendor session id.</summary>
    public static string? FindAgentIdBySession(DaemonStore store, string sessionId) {
        if (WorkContextIds.CanonicalSessionId(sessionId) is not { } wanted || !Directory.Exists(store.Directory)) return null;

        foreach (var daemonDirectory in Directory.EnumerateDirectories(store.Directory)) {
            var resume = Path.Combine(daemonDirectory, "resume");

            foreach (var candidate in new[] { new ResumeLedgerStore(resume), new ResumeLedgerStore(ParticipantsDirectory(resume)) }) {
                try {
                    foreach (var entry in candidate.ReadAll())
                        if (Runs(entry, wanted)) return entry.AgentId;
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // One unreadable daemon directory must not hide an entry in the next.
                }
            }
        }

        return null;
    }

    static bool Runs(ResumeLedgerEntry entry, string canonicalSession) =>
        string.Equals(WorkContextIds.CanonicalSessionId(entry.SessionId), canonicalSession, StringComparison.OrdinalIgnoreCase)
     || string.Equals(WorkContextIds.CanonicalSessionId(entry.VendorSession), canonicalSession, StringComparison.OrdinalIgnoreCase);

    static ResumeLedgerEntry? ReadFile(string path) {
        string json;

        try {
            json = File.ReadAllText(path);
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return null;
        }

        ResumeLedgerEntry? entry;

        try {
            entry = JsonSerializer.Deserialize(json, ResumeLedgerJsonContext.Default.ResumeLedgerEntry);
        } catch (JsonException) {
            entry = null;
        }

        if (entry is null || string.IsNullOrEmpty(entry.AgentId) || entry.Launch is null) {
            Quarantine(path);
            return null;
        }

        return entry.SchemaVersion > ResumeLedgerEntry.CurrentSchemaVersion
            ? entry with { State = ResumeState.Interrupted, LastError = NewerSchemaReason }
            : entry;
    }

    static void Quarantine(string path) {
        try {
            File.Move(path, path + ".corrupt", overwrite: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Best effort: an entry that stays put is quarantined again on the next read.
        }
    }
}
