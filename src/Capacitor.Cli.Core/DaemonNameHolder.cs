using System.Collections.Frozen;
using System.Text.Json;

namespace Capacitor.Cli.Core;

/// <summary>Daemon names other machines on this account hold. The server refuses a second daemon with
/// a held name, so setup offers only a free one. Best-effort: an unreachable server or an unreadable
/// answer finds nothing held.
/// The local id is the one the daemon reports, from <see cref="MachineId"/>, not the profile's memory-tagging id.</summary>
public static class DaemonNameHolder {
    /// <summary>This machine's name, in the form a daemon name takes.</summary>
    public static string MachineDefault() => DaemonStore.Sanitize(Environment.MachineName);

    /// <summary><paramref name="preferred"/> when no other machine holds it, else the first of
    /// <c>preferred-1</c>, <c>preferred-2</c>, … that none does.</summary>
    public static string FirstFree(string preferred, IReadOnlyDictionary<string, DaemonNameHolding> held) {
        if (!held.ContainsKey(preferred)) return preferred;

        for (var n = 1; ; n++) {
            var candidate = $"{preferred}-{n}";
            if (!held.ContainsKey(candidate)) return candidate;
        }
    }

    public static async Task<IReadOnlyDictionary<string, DaemonNameHolding>> HeldElsewhereAsync(
            HttpClient http, string serverUrl, ConfigRoot config, CancellationToken ct = default) {
        try {
            var machine        = new MachineId(config);
            var localMachineId = machine.ReadPersisted();
            // An id that exists but cannot be read right now could be the holder's own; that proves nothing.
            if (localMachineId is null && machine.IsPersisted) return FrozenDictionary<string, DaemonNameHolding>.Empty;

            using var resp = await http.GetAsync($"{serverUrl.TrimEnd('/')}/api/daemons", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return FrozenDictionary<string, DaemonNameHolding>.Empty;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.IsArray) return FrozenDictionary<string, DaemonNameHolding>.Empty;

            var held = new Dictionary<string, DaemonNameHolding>(StringComparer.Ordinal);

            foreach (var daemon in doc.RootElement.EnumerateArray()) {
                if (daemon.Str("name") is not { } name || daemon.Bool("connected") != true) continue;

                // A holder reporting no machine id may be this machine's own older daemon, so only two
                // known, different ids — or a machine that has never run a daemon — prove a collision.
                var machineId = daemon.Str("machine_id");
                if (localMachineId is not null && (machineId is null || machineId == localMachineId)) continue;

                held[name] = new DaemonNameHolding(daemon.Str("platform") ?? "unknown platform", daemon.Str("version"));
            }

            return held;
        } catch (Exception) when (!ct.IsCancellationRequested) {
            return FrozenDictionary<string, DaemonNameHolding>.Empty;
        }
    }
}
