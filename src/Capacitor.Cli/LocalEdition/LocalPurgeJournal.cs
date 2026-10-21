using System.Globalization;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>The purge operation's journal vocabulary. <see cref="Retiring"/> is written before the registry moves the
/// installation to its retired list, <see cref="Retired"/> once that is durable; nothing is deleted before then. Both
/// deny the server a start.</summary>
static class LocalPurgeJournal {
    public const string Operation = "purge";

    public const string Retiring = "retiring";
    public const string Retired  = "retired";

    public const string PortKey      = "port";
    public const string ServerUrlKey = "server_url";
    public const string ConfigDirKey = "config_dir";
    public const string WatcherDirKey = "watcher_dir";

    public static IReadOnlyDictionary<string, string> Build(LocalInstallation installation, string configDir, string watcherDir) =>
        new Dictionary<string, string>(StringComparer.Ordinal) {
            [PortKey]      = installation.Port.ToString(CultureInfo.InvariantCulture),
            [ServerUrlKey] = $"http://localhost:{installation.Port.ToString(CultureInfo.InvariantCulture)}",
            [ConfigDirKey] = configDir,
            [WatcherDirKey] = watcherDir,
        };

    /// <summary>False, with the offending field named in <paramref name="error"/>, for anything this build would not
    /// have written.</summary>
    public static bool TryRead(LifecycleJournalEntry entry, out PurgeDetails details, out string error) {
        details = null!;
        error   = Check(entry, out var port);

        if (error.Length > 0) return false;

        details = new(entry.InstallationId!, port, entry.Details[ServerUrlKey], entry.Details[ConfigDirKey], entry.Details[WatcherDirKey]);

        return true;
    }

    static string Check(LifecycleJournalEntry entry, out int port) {
        port = 0;

        if (entry.Operation != Operation) return $"operation: \"{entry.Operation}\" is not {Operation}";
        if (entry.Phase is not (Retiring or Retired)) return $"phase: \"{entry.Phase}\" is unknown";
        if (entry.ServerStart != ServerStart.Deny) return $"server_start: does not match the phase \"{entry.Phase}\"";

        if (entry.InstallationId is not { Length: 32 } id || !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return "installation_id: not 32 lowercase hex characters";

        var d = entry.Details;

        if (!d.TryGetValue(PortKey, out var portText)
         || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
         || port is < 1 or > 65535)
            return "port: not between 1 and 65535";

        if (!d.TryGetValue(ServerUrlKey, out var url) || url != $"http://localhost:{port}")
            return $"server_url: not http://localhost:{port}";

        if (!d.TryGetValue(ConfigDirKey, out var configDir) || !Path.IsPathFullyQualified(configDir))
            return "config_dir: not an absolute path";

        if (!d.TryGetValue(WatcherDirKey, out var watcherDir) || !Path.IsPathFullyQualified(watcherDir))
            return "watcher_dir: not an absolute path";

        return "";
    }
}
