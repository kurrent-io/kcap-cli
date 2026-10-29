using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services.Update;

/// <summary>
/// What an update leaves for the process that replaces the app: the route it took and the version it expects
/// to come back as. The old process saves it before it exits and the new one takes it once at startup, which is
/// how a launch knows it finishes an update, and whether the update applied at all: Velopack relaunches the old
/// version after a failed apply, so a relaunch alone proves nothing.
/// </summary>
public sealed record AppUpdateTransition(string Id, string Entry, string? From, string To, DateTimeOffset StartedAt) {
    public const string FileName = "app-update-transition.json";
    public const string InApp = "in_app";
    public const string PendingAtStartup = "pending_at_startup";
    public const string Installer = "installer";

    /// Past this the record belongs to a handover that never reached a new process.
    internal static readonly TimeSpan Freshness = TimeSpan.FromMinutes(30);

    public static AppUpdateTransition Begin(string entry, string? from, string to, TimeProvider time) =>
        new(Guid.NewGuid().ToString("N")[..12], entry, from, to, time.GetUtcNow());

    /// <summary>Whether the running app is the version this update installs; build metadata is ignored.</summary>
    public bool AppliedTo(string runningVersion) => PrereleaseSemver.Compare(Core(To), Core(runningVersion)) == 0;

    static string Core(string version) => version.Split('+', 2)[0];

    public void Save(ConfigRoot config) {
        Directory.CreateDirectory(config.Directory);
        var json = new JsonObject {
            ["id"] = Id, ["entry"] = Entry, ["from"] = From, ["to"] = To, ["started_at"] = StartedAt.ToString("o"),
        }.ToJsonString();
        var path = config.Path(FileName);
        var staging = path + "." + Guid.NewGuid().ToString("N");
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(staging, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(json);
            File.Move(staging, path, true);
        } finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    /// <summary>Reads and removes the record; null when there is none, it cannot be read, or it is stale.</summary>
    public static AppUpdateTransition? Take(ConfigRoot config, TimeProvider time) {
        var path = config.Path(FileName);
        try {
            if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null) return null;
            var node = JsonNode.Parse(File.ReadAllText(path));
            var transition = node?["to"]?.GetValue<string>() is { Length: > 0 } to
                && node["id"]?.GetValue<string>() is { Length: > 0 } id
                && node["entry"]?.GetValue<string>() is { Length: > 0 } entry
                && DateTimeOffset.TryParse(node["started_at"]?.GetValue<string>(), out var started)
                ? new AppUpdateTransition(id, entry, node["from"]?.GetValue<string>(), to, started)
                : null;
            var age = time.GetUtcNow() - transition?.StartedAt;
            return age is { } a && a >= TimeSpan.Zero && a < Freshness ? transition : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) {
            return null;
        } finally {
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
