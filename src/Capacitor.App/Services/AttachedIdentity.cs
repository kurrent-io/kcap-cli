using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Whether the daemon the app attached to by name is the one its own resolution expects. A daemon built
/// before <see cref="DaemonInfoDto.Profile"/> existed, which an installed service can still be running,
/// reports no profile and is judged on its name and server alone.
internal static class AttachedIdentity {
    /// What differs, a headline and then the detail, or null when nothing the app can see does.
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
        return actual.Count == 0 ? null
            : $"Daemon {expectedName} doesn't match this app. It's running for {string.Join(", ", actual)}, not {string.Join(", ", expected)}.";
    }

    /// Two servers named so a reader can tell them apart: by host, or in full when only the scheme, port or
    /// path differs.
    public static (string First, string Second) ServerLabels(string first, string second) {
        var (firstHost, secondHost) = (Host(first), Host(second));
        return !string.Equals(firstHost, secondHost, StringComparison.OrdinalIgnoreCase)
            ? (firstHost, secondHost)
            : (ServerIdentity.Canonicalize(first) ?? first, ServerIdentity.Canonicalize(second) ?? second);
    }

    /// Only launchd and the Windows scheduler support the verified replace the app offers.
    public static bool SupportsVerifiedReplace => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    public static string ManualRepair(string daemonName, string? profile) =>
        ManualRepair(daemonName, profile, OperatingSystem.IsWindows(), SupportsVerifiedReplace);

    /// The fix when the app cannot offer one: the unit captures the invoking environment, so a server
    /// override inherited from the shell would be baked into the replacement and outrank the profile again.
    /// The commands come last, in backticks, so a notice can lift them out as copyable code.
    /// The install keeps the configured name, which the daemon reports, unless it would read as a flag; the
    /// uninstall targets the service id. On Windows the quoting is PowerShell's, which cmd.exe does not share.
    internal static string ManualRepair(string daemonName, string? profile, bool windows, bool verifiedReplace) {
        var serviceId = DaemonStore.Sanitize(daemonName);
        var name      = string.IsNullOrWhiteSpace(daemonName) || daemonName.TrimStart().StartsWith('-') ? serviceId : Quote(daemonName, windows);
        var target    = $"--name {name} --profile {(profile is null ? "<profile>" : Quote(profile, windows))}";
        var commands  = verifiedReplace
            ? $"`kcap daemon service install --replace --verify {target}`"
            : $"`kcap daemon service uninstall --name {serviceId}` `kcap daemon service install {target}`";
        var placeholder = profile is null ? "Replace <profile> with a profile for this app's server. " : "";
        var these       = verifiedReplace ? "this" : "these";
        return $"{placeholder}Run {these} in {(windows ? "PowerShell" : "a shell")} without KCAP_URL set, then restart kcap: {commands}";
    }

    // Quoted only when the value needs it.
    static string Quote(string value, bool windows) {
        if (value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return value;
        // PowerShell ends a single-quoted string at any of its four single quotes, typographic ones included.
        return windows
            ? "'" + string.Concat(value.Select(c => c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B' ? $"{c}{c}" : c.ToString())) + "'"
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    static string Host(string server) =>
        Uri.TryCreate(server, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : server;
}
