using System.Text.Json;

namespace Capacitor.Cli.Core;

/// <summary>
/// Stable per-machine identifier (AI-1207): lets the server prove that a daemon reconnecting
/// under a given repo path is actually running on the machine it claims, rather than trusting a
/// path string alone. Persisted once to <c>machine.json</c> in the CLI's config directory
/// (<see cref="PathHelpers.ConfigPath"/> — the same resolution <c>Auth.TokenStore</c> uses,
/// honouring <c>KCAP_CONFIG_DIR</c>) so every process on this machine — hooks, watcher, daemon,
/// MCP — resolves the same file and reports the same id.
///
/// Distinct from <see cref="MachineIdProvider"/> (AI-1134), which tags memories with a
/// separately-generated id stored inside the profile config (<c>config.json</c>'s
/// <c>machine_id</c>); the two are not reconciled. This one is a standalone file so reading/
/// writing it can never race a concurrent <c>ProfileConfig</c> save.
/// </summary>
public static class MachineId {
    static readonly string MachinePath = PathHelpers.ConfigPath("machine.json");

    /// <summary>
    /// Returns this machine's stable id, generating and persisting one on first call. Later
    /// calls — in this process or a new one — read the same persisted value back.
    /// </summary>
    public static string Get() => ReadPersisted() ?? Create();

    /// <summary>
    /// Reads the persisted id straight off disk — what a fresh process (or a fresh call after a
    /// peer process wrote it) would see. Returns null if machine.json doesn't exist yet or is
    /// corrupt (never resurrects a partial/garbled write; the next <see cref="Get"/> just
    /// regenerates).
    /// </summary>
    public static string? ReadPersisted() {
        if (!File.Exists(MachinePath)) return null;

        try {
            var json = File.ReadAllText(MachinePath);
            var file = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.MachineIdFile);
            return string.IsNullOrWhiteSpace(file.Id) ? null : file.Id;
        } catch (JsonException) {
            return null;
        }
    }

    // Two processes can race to create machine.json on a brand-new machine (e.g. the watcher and
    // the daemon starting at once). FileMode.CreateNew is an exclusive OS-level create — exactly
    // one writer succeeds; the loser's create throws IOException and re-reads the file the
    // winner just wrote instead of keeping its own generated id, so both converge on one value
    // with no separate lock file needed.
    static string Create() {
        var id  = Guid.NewGuid().ToString("N");
        var dir = Path.GetDirectoryName(MachinePath)!;
        Directory.CreateDirectory(dir);

        try {
            using var stream = new FileStream(MachinePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(JsonSerializer.Serialize(new MachineIdFile(id), CapacitorJsonContext.Default.MachineIdFile));
            return id;
        } catch (IOException) {
            // Lost the race: a peer created machine.json between our ReadPersisted() check
            // (inside Get()) and this create. Adopt whichever id won.
            return ReadPersisted() ?? id;
        }
    }
}

/// <summary>On-disk shape of <c>machine.json</c> (see <see cref="MachineId"/>).</summary>
public readonly record struct MachineIdFile(string Id);
