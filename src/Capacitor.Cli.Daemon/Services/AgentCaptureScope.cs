using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Whether an UNATTENDED hosted agent may report to the server at all. A reviewer runs where a
/// flow points it, so nobody opted that directory in; its prompt, terminal output and transcript
/// would upload regardless. An agent a human launched here is a different thing entirely and is
/// never asked — choosing the directory is the consent.
/// <para>Judged on the ORIGINATING checkout, never the execution directory: an independent
/// snapshot runs in a daemon-owned copy that matches nothing a user configured, so scoping it on
/// where it executes would drop every snapshot under an allowlist and catch none under a
/// denylist.</para>
/// </summary>
internal static class AgentCaptureScope {
    /// <summary>
    /// Sent to the server when a launch is refused. Names no path and no repository on purpose:
    /// the identity of an out-of-scope checkout is exactly what the profile's lists exist to keep
    /// off the server, so a reason that carried it would upload the one thing the refusal is
    /// protecting. The specifics go to the daemon's own log instead.
    /// </summary>
    internal const string RefusalReason =
        "out_of_capture_scope: this checkout is outside the profile's capture scope";

    /// <summary>
    /// Whether the profile scopes anything at all. Checked before <see cref="IsOutOfScope"/> so a
    /// profile with no lists — the default — costs nothing on the launch path and never resolves a
    /// home directory or a repository it has no use for.
    /// </summary>
    internal static bool Configured(Profile? profile)
        => profile is not null
        && (profile.AllowedPaths  is { Length: > 0 } || profile.ExcludedPaths  is { Length: > 0 }
         || profile.AllowedRepos  is { Length: > 0 } || profile.ExcludedRepos  is { Length: > 0 });

    /// <summary>
    /// True when <paramref name="originCheckout"/> falls outside the profile's capture scope.
    /// Repo resolution is skipped entirely when neither repo list is configured, so the common
    /// case reads no <c>.git/config</c> at all.
    /// </summary>
    internal static bool IsOutOfScope(string? originCheckout, Profile? profile, UserHome home) {
        if (PathExclusion.IsOutOfScope(
                originCheckout, profile?.AllowedPaths, profile?.ExcludedPaths, home)) {
            return true;
        }

        var allowedRepos  = profile?.AllowedRepos;
        var excludedRepos = profile?.ExcludedRepos;

        if (allowedRepos is not { Length: > 0 } && excludedRepos is not { Length: > 0 }) return false;

        return RepoScope.IsOutOfScope(
            GitRepoKey.ForCheckout(originCheckout), allowedRepos, excludedRepos);
    }
}
