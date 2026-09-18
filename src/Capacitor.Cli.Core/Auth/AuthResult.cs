namespace Capacitor.Cli.Core.Auth;

/// <summary>One profile the commit boundary will publish, with the canonical server it is bound to.</summary>
public sealed record AuthIdentity(string Profile, string CanonicalServer);

/// <summary>
/// Why an operation failed, for callers that must react differently per cause (the setup funnel
/// distinguishes a denied sign-in from a tenant-less account). <see cref="Other"/> carries no claim.
/// </summary>
/// <remarks>
/// <see cref="ProvisioningInProgress"/> is not really a failure: the workspace is being created and
/// the poll simply outlived its window. It rides <see cref="AuthResult.Failed"/> because nothing
/// durable was published, the same way <see cref="SigninDenied"/> does — but a caller that headlines
/// it as an error is telling the user something untrue.
/// </remarks>
public enum AuthFailureReason { Other, Unreachable, SigninDenied, NoTenantsFound, ProvisioningInProgress }

/// <summary>
/// What a sign-in found, for a caller that has to ask someone which workspace to use before it can
/// name one. Nothing here was published: the commit boundary is not entered on this route.
/// </summary>
/// <param name="CanCreate">Whether this account may create a workspace. Only the hosted lane
/// provisions, and only for an account that belongs to none.</param>
public sealed record DiscoveryReport(
        DiscoveredTenant[] Tenants,
        string             Provider,
        bool               CanCreate,
        string?            Error  = null,
        AuthFailureReason  Reason = AuthFailureReason.Other) {
    public static DiscoveryReport Failure(
            string provider, string error, AuthFailureReason reason = AuthFailureReason.Other) =>
        new([], provider, CanCreate: false, error, reason);
}

/// <summary>
/// Outcome of an onboarding operation. <see cref="Cancelled"/> is strictly pre-boundary — once the
/// boundary is entered every publication runs to completion and the answer is
/// <see cref="Committed"/>, never a torn stop. <see cref="Retarget"/> is the WorkOS "I already have
/// a workspace" answer: nothing durable, and the caller resolves the input against its own server.
/// </summary>
public abstract record AuthResult {
    public sealed record Committed(
        string                      ActiveProfile,
        string                      CanonicalServer,
        string                      Provider,
        string?                     Username,
        IReadOnlyList<AuthIdentity> Published) : AuthResult;

    public sealed record Cancelled : AuthResult;

    /// <param name="Message">Rendered through <see cref="IAuthProgress"/> and carried for callers that
    /// log or re-present it — except for <see cref="AuthFailureReason.ProvisioningInProgress"/>, whose
    /// message is composed for a headline and complements the sink line rather than repeating it.</param>
    public sealed record Failed(string Message, AuthFailureReason Reason = AuthFailureReason.Other) : AuthResult;

    public sealed record Retarget(string ServerInput) : AuthResult;
}
