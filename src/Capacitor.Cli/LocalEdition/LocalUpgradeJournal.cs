using System.Globalization;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>The upgrade operation's journal vocabulary. Before <see cref="Switching"/> the old version is selected and
/// a recovery restarts it; from <see cref="Switching"/> on the registry may already name the new one, so a recovery only
/// ever finishes the switch.</summary>
static class LocalUpgradeJournal {
    public const string Operation = "upgrade";

    /// <summary>The new bundle is being staged beside the old one, which keeps running.</summary>
    public const string Staging = "staging";

    /// <summary>The unit is being unloaded; the server must not start again under it.</summary>
    public const string Stopping = "stopping";

    /// <summary>The old version is being started again after a failure before the switch.</summary>
    public const string Restarting = "restarting";

    /// <summary>The registry and <c>bin/current</c> are being moved to the new version.</summary>
    public const string Switching = "switching";

    /// <summary>The new version is selected and its unit is being started.</summary>
    public const string Starting = "starting";

    public const string FromKey       = "from_version";
    public const string ToKey         = "to_version";
    public const string PortKey       = "port";
    public const string WasRunningKey = "was_running";

    public static IReadOnlyDictionary<string, string> Build(UpgradeDetails details) =>
        new Dictionary<string, string>(StringComparer.Ordinal) {
            [FromKey]       = details.From,
            [ToKey]         = details.To,
            [PortKey]       = details.Port.ToString(CultureInfo.InvariantCulture),
            [WasRunningKey] = details.WasRunning ? "true" : "false",
        };

    public static ServerStart StartFor(string phase) =>
        phase is Staging or Restarting or Starting ? ServerStart.Allow : ServerStart.Deny;

    /// <summary>False, with the offending field named in <paramref name="error"/>, for anything this build would
    /// not have written.</summary>
    public static bool TryRead(LifecycleJournalEntry entry, out UpgradeDetails details, out string error) {
        details = null!;
        error   = Check(entry, out var port);

        if (error.Length > 0) return false;

        var d = entry.Details;
        details = new(d[FromKey], d[ToKey], port, d[WasRunningKey] == "true");

        return true;
    }

    static string Check(LifecycleJournalEntry entry, out int port) {
        port = 0;

        if (entry.Operation != Operation) return $"operation: \"{entry.Operation}\" is not {Operation}";

        if (entry.Phase is not (Staging or Stopping or Restarting or Switching or Starting)) return $"phase: \"{entry.Phase}\" is unknown";
        if (entry.ServerStart != StartFor(entry.Phase)) return $"server_start: does not match the phase \"{entry.Phase}\"";

        var d = entry.Details;

        foreach (var key in new[] { FromKey, ToKey }) {
            if (!d.TryGetValue(key, out var version) || !ReleaseManifest.IsValidVersion(version) || version.Any(char.IsWhiteSpace))
                return $"{key}: not a release version";
        }

        if (!d.TryGetValue(PortKey, out var portText)
         || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
         || port is < 1 or > 65535)
            return "port: not between 1 and 65535";

        if (!d.TryGetValue(WasRunningKey, out var running) || running is not ("true" or "false"))
            return "was_running: not true or false";

        return "";
    }
}
