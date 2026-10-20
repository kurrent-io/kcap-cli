using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Whether the daemon the app attached to by name is the one its own resolution expects. A daemon built
/// before <see cref="DaemonInfoDto.Profile"/> existed, which an installed service can still be running,
/// reports no profile and is judged on its name and server alone.
internal static class AttachedIdentity {
    /// What differs, as one sentence, or null when nothing the app can see does.
    public static string? Mismatch(DaemonInfoDto daemon, string expectedName, string? expectedServer, string? expectedProfile) {
        var actual   = new List<string>();
        var expected = new List<string>();
        if (!string.IsNullOrEmpty(expectedServer) && !ServerIdentity.Matches(daemon.ServerUrl, expectedServer)) {
            var (daemonLabel, expectedLabel) = ServerLabels(daemon.ServerUrl, expectedServer);
            actual.Add(daemonLabel);
            expected.Add(expectedLabel);
        }
        if (daemon.Profile is { Length: > 0 } profile && expectedProfile is { Length: > 0 } && !string.Equals(profile, expectedProfile, StringComparison.Ordinal)) {
            actual.Add($"profile {profile}");
            expected.Add($"profile {expectedProfile}");
        }
        // Compared as the socket and the service unit name it, which is how the app found this daemon.
        if (DaemonStore.Sanitize(daemon.Name) != DaemonStore.Sanitize(expectedName)) {
            actual.Add($"name {daemon.Name}");
            expected.Add($"name {expectedName}");
        }
        return actual.Count == 0 ? null : $"Daemon {expectedName} is running for {string.Join(", ", actual)}, not {string.Join(", ", expected)}.";
    }

    /// Two servers named so a reader can tell them apart: by host, or in full when only the scheme, port or
    /// path differs.
    public static (string First, string Second) ServerLabels(string first, string second) {
        var (firstHost, secondHost) = (Host(first), Host(second));
        return !string.Equals(firstHost, secondHost, StringComparison.OrdinalIgnoreCase)
            ? (firstHost, secondHost)
            : (ServerIdentity.Canonicalize(first) ?? first, ServerIdentity.Canonicalize(second) ?? second);
    }

    /// The fix when the app cannot offer one: the unit captures the invoking environment, so a server
    /// override inherited from the shell would be baked into the replacement and outrank the profile again.
    /// Only launchd and the Windows scheduler support the verified replace.
    public static string ManualRepair(string daemonName, string? profile) {
        var flags = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? " --replace --verify" : "";
        return $"To fix it, run `kcap daemon service install{flags} --name {Quote(daemonName)} --profile {(profile is null ? "PROFILE" : Quote(profile))}` "
             + "in a shell where KCAP_URL is unset, then restart the app.";
    }

    // Quoted only when the value needs it, in the convention of the shell the user is likely to paste into.
    static string Quote(string value) {
        if (value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return value;
        return OperatingSystem.IsWindows()
            ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    static string Host(string server) =>
        Uri.TryCreate(server, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : server;
}
