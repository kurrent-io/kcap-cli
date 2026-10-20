using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Whether the daemon the app attached to by name is the one its own resolution expects. A daemon that
/// reports no profile, which an older one does, is judged on its name and server alone.
internal static class AttachedIdentity {
    /// What differs, as one sentence, or null when nothing the app can see does.
    public static string? Mismatch(DaemonInfoDto daemon, string expectedName, string? expectedServer, string? expectedProfile) {
        var actual   = new List<string>();
        var expected = new List<string>();
        if (!string.IsNullOrEmpty(expectedServer) && !ServerIdentity.Matches(daemon.ServerUrl, expectedServer)) {
            actual.Add(Host(daemon.ServerUrl));
            expected.Add(Host(expectedServer));
        }
        if (daemon.Profile is { Length: > 0 } profile && expectedProfile is { Length: > 0 } && !string.Equals(profile, expectedProfile, StringComparison.Ordinal)) {
            actual.Add($"profile {profile}");
            expected.Add($"profile {expectedProfile}");
        }
        if (!string.Equals(daemon.Name, expectedName, StringComparison.Ordinal)) {
            actual.Add($"name {daemon.Name}");
            expected.Add($"name {expectedName}");
        }
        return actual.Count == 0 ? null : $"Daemon {expectedName} is running for {string.Join(", ", actual)}, not {string.Join(", ", expected)}.";
    }

    /// The fix when the app cannot offer one: the unit captures the invoking environment, so a server
    /// override inherited from the shell would be baked into the replacement and outrank the profile again.
    public static string ManualRepair(string daemonName, string? profile) =>
        $"To fix it, run `kcap daemon service install --replace --verify --name {daemonName} --profile {profile ?? "<profile>"}` "
      + "in a shell where KCAP_URL is unset, then restart the app.";

    static string Host(string server) =>
        Uri.TryCreate(server, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : server;
}
