using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>A daemon already connected under a name from another machine on this account. The server
/// refuses a second daemon with that name, so setup checks before saving it. Best-effort: an
/// unreachable server or an unreadable answer finds nothing.
/// The local id is the one the daemon reports, from <see cref="MachineId"/>, not the profile's memory-tagging id.</summary>
static class DaemonNameHolder {
    public static async Task<(string Platform, string? Version)?> FindElsewhereAsync(
            HttpClient http, string serverUrl, string name, ConfigRoot config, CancellationToken ct = default) {
        try {
            var localMachineId = new MachineId(config).ReadPersisted();

            using var resp = await http.GetAsync($"{serverUrl.TrimEnd('/')}/api/daemons", ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var daemon in doc.RootElement.EnumerateArray()) {
                if (daemon.ValueKind != JsonValueKind.Object ||
                    Text(daemon, "name") != name ||
                    !(daemon.TryGetProperty("connected", out var connected) && connected.ValueKind == JsonValueKind.True))
                    continue;

                // A holder reporting no machine id may be this machine's own older daemon, so only two
                // known, different ids — or a machine that has never run a daemon — prove a collision.
                var machineId = Text(daemon, "machine_id");
                if (localMachineId is not null && (machineId is null || machineId == localMachineId)) continue;

                return (Text(daemon, "platform") ?? "unknown platform", Text(daemon, "version"));
            }

            return null;
        } catch (Exception) when (!ct.IsCancellationRequested) {
            return null;
        }
    }

    static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
