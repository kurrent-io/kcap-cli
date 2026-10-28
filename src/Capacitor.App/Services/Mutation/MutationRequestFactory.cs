using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.App.Services.Mutation;

/// The one place a MutationRequest is built: a daemon verb whose caller cannot bind a canonical server
/// never reaches the executor — it fails closed to Refused before any MutationRequest exists.
/// Canonicalization is idempotent, so callers may pass either a raw configured URL or an already-canonical one.
public static class MutationRequestFactory {
    /// Non-null return means refused (no request built); null return means `request` is usable.
    public static MutationOutcome? TryBuild(
            MutationVerb verb, string? profileName, string? serverUrl, string daemonName, out MutationRequest? request,
            string? retireServiceId = null) {
        var canonical = ServerIdentity.Canonicalize(serverUrl);
        if (string.IsNullOrWhiteSpace(profileName) || canonical is null) {
            request = null;
            return new MutationOutcome.Refused("no_server_configured", RecoverySurface.Attention);
        }

        if (retireServiceId is not null && (verb != MutationVerb.Replace ||
                string.IsNullOrWhiteSpace(retireServiceId) || DaemonStore.Sanitize(retireServiceId) == DaemonStore.Sanitize(daemonName))) {
            request = null;
            return new MutationOutcome.Refused("invalid_retire_target", RecoverySurface.Attention);
        }

        request = new MutationRequest(verb, profileName, canonical, daemonName,
            retireServiceId is null ? null : DaemonStore.Sanitize(retireServiceId));
        return null;
    }

    /// The account verbs touch nothing on the server, so the request carries none. Nothing reads RequestId:
    /// it exists so two account actions, equal or not, never coalesce.
    public static MutationRequest ForAccount(MutationVerb verb, AccountArgs args, string daemonName, string? profileName) {
        if (!verb.IsAccountVerb()) throw new ArgumentOutOfRangeException(nameof(verb), verb, "not an account verb");

        return new MutationRequest(verb, profileName ?? "", "", daemonName, Account: args, RequestId: Guid.NewGuid().ToString("N"));
    }
}
