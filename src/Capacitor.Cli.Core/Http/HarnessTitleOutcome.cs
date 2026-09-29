namespace Capacitor.Cli.Core.Http;

public enum HarnessTitleOutcome {
    Posted,

    /// <summary>A coded 404 — the session is not visible to this caller, or not yet projected.</summary>
    SessionNotFound,

    /// <summary>A bare 404 — an older server without the route.</summary>
    RouteMissing,

    /// <summary>A 4xx other than the coded 404 — the request was rejected on its merits (blank
    /// title, unsafe id, not the owner).</summary>
    Refused,

    Failed,
}
