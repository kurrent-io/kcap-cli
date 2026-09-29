namespace Capacitor.Cli.Core.Http;

public enum HarnessTitleOutcome {
    Posted,

    /// <summary>A coded 404 — the session is not visible to this caller, or not yet projected.</summary>
    SessionNotFound,

    /// <summary>A bare 404 — an older server without the route.</summary>
    RouteMissing,

    /// <summary>A 400, 403 or 422 — the request was rejected on its merits (blank title, unsafe id, not the
    /// owner), so resending the same value cannot succeed.</summary>
    Refused,

    /// <summary>A transport fault, an auth lapse, a timeout, a rate limit or a server fault — worth a retry.</summary>
    Failed,
}
