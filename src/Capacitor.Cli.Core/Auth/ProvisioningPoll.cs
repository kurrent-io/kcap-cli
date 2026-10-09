namespace Capacitor.Cli.Core.Auth;

// What the create-tenant poll should do with a single status-poll result.
public enum PollVerdict {
    Active,      // live and linked to an org — done
    ActiveNoOrg, // live but no WorkOS org (can't org-switch) — terminal, needs support
    Failed,      // server marked the row failed — terminal
    Forbidden,   // 403 — email unverified — terminal
    NotFound,    // 404 — slug isn't owned by this account — terminal
    Wait         // still provisioning, or a transient/auth blip the next tick recovers from
}

// Pure decision for the interactive poll, extracted from the Spectre-coupled provisioner so
// every branch is unit-tested. The old poll only reacted to active+orgId and failed; every
// other response (reserved, 401/403/404, transport error) silently looped until timeout.
public static class ProvisioningPoll {
    public static PollVerdict Classify(int statusCode, string? state, string? workosOrgId) => statusCode switch {
        200 when state == "active" => workosOrgId is { Length: > 0 } ? PollVerdict.Active : PollVerdict.ActiveNoOrg,
        200 when state == "failed" => PollVerdict.Failed,
        403                        => PollVerdict.Forbidden,
        404                        => PollVerdict.NotFound,
        // 200 provisioning/reserved, 401 (token source refreshes next tick), 0 transport, 5xx: keep waiting.
        _                          => PollVerdict.Wait
    };

    /// <summary>Only a status the server answered with says the workspace is still being built; a
    /// transport error or a 5xx says nothing.</summary>
    public static bool IsPending(int statusCode, string? state) =>
        statusCode == 200 && state is not null && state is not ("active" or "failed");
}
