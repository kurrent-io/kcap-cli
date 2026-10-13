using System.Globalization;
using Capacitor.Cli.Core.Harness.Grok;

namespace Capacitor.Cli.Harness.Grok;

/// <summary>
/// Whether this hook runs inside the Grok the daemon hosts, whose session the daemon already records. The
/// daemon names itself in <see cref="GrokHookEnvironment.HostedVar"/>, which every process Grok starts
/// inherits, so the hook stands down only when the process running it is the daemon's direct child, or a
/// shell directly under that child, which is how Grok runs a hook command. Process names other than the
/// shell's are not consulted, because <c>KCAP_GROK_PATH</c> may name a build with any name. A grok the
/// hosted agent starts itself sits between the hook and the daemon's child, so it keeps recording.
/// </summary>
static class GrokHostedSession {
    static readonly HashSet<string> HookShells = new(StringComparer.OrdinalIgnoreCase) { "sh", "bash", "dash", "zsh", "cmd", "powershell", "pwsh" };

    public static bool IsDaemonHosted() =>
        ProcessHelpers.GetParentPid() is { } hookParent
     && IsDaemonHosted(Environment.GetEnvironmentVariable(GrokHookEnvironment.HostedVar), hookParent, ProcessHelpers.GetProcessInfo);

    internal static bool IsDaemonHosted(
            string?                                             hostedVar,
            int                                                 hookParentPid,
            Func<int, (int ppid, IReadOnlyList<string> names)?> lookup
        ) {
        if (!int.TryParse(hostedVar, NumberStyles.None, CultureInfo.InvariantCulture, out var daemonPid) || daemonPid <= 1) return false;
        if (lookup(hookParentPid) is not { } parent) return false;
        if (parent.ppid == daemonPid) return true;

        return parent.names.Any(IsHookShell) && lookup(parent.ppid)?.ppid == daemonPid;
    }

    static bool IsHookShell(string name) => HookShells.Contains(Path.GetFileNameWithoutExtension(name.Replace('\\', '/')));
}
