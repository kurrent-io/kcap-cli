using System.Globalization;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>The setup operation's journal vocabulary: its phases, the details it records, and the check that an
/// entry is one this build wrote.</summary>
static class LocalSetupJournal {
    public const string Operation = LocalSetupState.Operation;

    public const string Pending    = "pending";
    public const string Unit       = "unit";
    public const string Committing = "committing";
    public const string Allowed    = "allowed";

    public const string PortKey      = "port";
    public const string VersionKey   = "version";
    public const string ServerUrlKey = "server_url";
    public const string ConfigDirKey = "config_dir";
    public const string ProfileKey   = "profile";

    public static IReadOnlyDictionary<string, string> Build(SetupDetails details) =>
        new Dictionary<string, string>(StringComparer.Ordinal) {
            [PortKey]      = details.Port.ToString(CultureInfo.InvariantCulture),
            [VersionKey]   = details.Version,
            [ServerUrlKey] = details.ServerUrl,
            [ConfigDirKey] = details.ConfigDir,
            [ProfileKey]   = details.Profile,
        };

    /// <summary>False, with the offending field named in <paramref name="error"/>, for anything this build would
    /// not have written. A recovery acts only on an entry that passes.</summary>
    public static bool TryRead(LifecycleJournalEntry entry, out SetupDetails details, out string error) {
        details = null!;
        error   = Check(entry, out var port);

        if (error.Length > 0) return false;

        details = new(entry.InstallationId!, port, entry.Details[VersionKey], entry.Details[ServerUrlKey],
            entry.Details[ConfigDirKey], entry.Details[ProfileKey]);

        return true;
    }

    static string Check(LifecycleJournalEntry entry, out int port) {
        port = 0;

        if (entry.Operation != Operation) return $"operation: \"{entry.Operation}\" is not {Operation}";

        if (entry.InstallationId is not { Length: 32 } id || !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return "installation_id: not 32 lowercase hex characters";

        var expected = entry.Phase switch {
            Pending or Unit or Committing => ServerStart.Fenced,
            Allowed                       => ServerStart.Allow,
            _                             => (ServerStart?)null,
        };

        if (expected is null) return $"phase: \"{entry.Phase}\" is unknown";
        if (entry.ServerStart != expected) return $"server_start: does not match the phase \"{entry.Phase}\"";

        var d = entry.Details;

        if (!d.TryGetValue(VersionKey, out var version) || !ReleaseManifest.IsValidVersion(version) || version.Any(char.IsWhiteSpace))
            return "version: not a release version";

        if (!d.TryGetValue(PortKey, out var portText)
         || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
         || port is < 1 or > 65535)
            return "port: not between 1 and 65535";

        if (!d.TryGetValue(ServerUrlKey, out var url) || url != $"http://localhost:{port}")
            return $"server_url: not http://localhost:{port}";

        if (!d.TryGetValue(ConfigDirKey, out var configDir) || !Path.IsPathFullyQualified(configDir))
            return "config_dir: not an absolute path";

        if (!d.TryGetValue(ProfileKey, out var profile) || !IsProfileName(profile))
            return "profile: not a valid profile name";

        return "";
    }

    // The token store's rule: a profile name becomes a file name.
    static bool IsProfileName(string name) =>
        !string.IsNullOrWhiteSpace(name)
     && name is not ("." or "..")
     && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
     && !name.Contains(Path.DirectorySeparatorChar)
     && !name.Contains(Path.AltDirectorySeparatorChar);
}
