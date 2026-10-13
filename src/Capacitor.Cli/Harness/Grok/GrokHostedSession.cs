using System.Globalization;
using Capacitor.Cli.Core.Harness.Grok;

namespace Capacitor.Cli.Harness.Grok;

/// <summary>
/// Whether this hook runs inside a Grok the daemon hosts, whose session the daemon already records. Every
/// process Grok starts inherits <see cref="GrokHookEnvironment.HostedVar"/>, so the marker alone is not
/// enough: the nearest grok on the ppid chain, past the <c>sh -c</c> Grok may run a hook through, must be
/// the daemon's direct child. A grok the hosted agent starts itself has another grok or a shell as parent.
/// </summary>
static class GrokHostedSession {
    public static bool IsDaemonHosted() =>
        ProcessHelpers.GetParentPid() is { } hookParent
     && IsDaemonHosted(Environment.GetEnvironmentVariable(GrokHookEnvironment.HostedVar), hookParent, ProcessHelpers.GetProcessInfo);

    internal static bool IsDaemonHosted(
            string?                                             hostedVar,
            int                                                 hookParentPid,
            Func<int, (int ppid, IReadOnlyList<string> names)?> lookup
        ) =>
        int.TryParse(hostedVar, NumberStyles.None, CultureInfo.InvariantCulture, out var daemonPid)
     && daemonPid > 1
     && ProcessHelpers.ResolveCodingAgentPid(hookParentPid, "grok", lookup) is { } grokPid
     && lookup(grokPid)?.ppid == daemonPid;
}
