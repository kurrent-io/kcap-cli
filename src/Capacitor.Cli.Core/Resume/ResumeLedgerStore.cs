using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Resume;

/// <summary>
/// Durable per-agent launch records under a daemon's <c>resume</c> directory. The file stem is a hash
/// of the agent id: the id crosses the wire unconstrained and must not reach a path. An unparseable
/// entry is renamed <c>.corrupt</c> and never acted on.
/// </summary>
public sealed class ResumeLedgerStore(string resumeDirectory, TimeProvider? time = null) {
    const string NewerSchemaReason = "ledger_newer_than_daemon";

    const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode OwnerOnlyFile      = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static ResumeLedgerStore For(DaemonStore store, string daemonName, TimeProvider? time = null) =>
        new(store.ResumeDirectory(daemonName), time);

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

        using (var writer = new StreamWriter(tempPath, options)) {
            writer.Write(JsonSerializer.Serialize(entry, ResumeLedgerJsonContext.Default.ResumeLedgerEntry));
        }

        File.Move(tempPath, finalPath, overwrite: true);
    }

    public bool Delete(string agentId) {
        var existed = false;

        foreach (var path in new[] { EntryPath(agentId), RingPath(agentId) }) {
            if (!File.Exists(path)) continue;

            File.Delete(path);
            existed = true;
        }

        return existed;
    }

    public ResumeLedgerEntry? TryRead(string agentId) => ReadFile(EntryPath(agentId));

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
            if (where is not null && !where(entry)) continue;

            Write(entry with { State = ResumeState.Interrupted, LastError = reason, UpdatedAt = (time ?? TimeProvider.System).GetUtcNow() });
            changed++;
        }

        return changed;
    }

    public static ResumeLedgerEntry? FindByAgentId(DaemonStore store, string agentId) {
        if (!Directory.Exists(store.Directory)) return null;

        foreach (var daemonDirectory in Directory.EnumerateDirectories(store.Directory)) {
            try {
                var found = new ResumeLedgerStore(Path.Combine(daemonDirectory, "resume")).TryRead(agentId);
                if (found is not null) return found;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // One unreadable daemon directory must not hide an entry in the next.
            }
        }

        return null;
    }

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
