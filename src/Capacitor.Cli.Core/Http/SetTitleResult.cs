namespace Capacitor.Cli.Core.Http;

public abstract record SetTitleResult {
    public sealed record Applied : SetTitleResult;

    /// <summary>The server kept the title it shows: a start title someone chose, or a later rename, outranks this one.</summary>
    public sealed record NotApplied(string? Shown) : SetTitleResult;

    /// <summary>An older server without <c>/hooks/agent-title</c> took the title through <c>/hooks/set-title</c>, which only
    /// fills a session with no title.</summary>
    public sealed record Unconfirmed : SetTitleResult;

    /// <summary>The server has no session by this id that the caller can see.</summary>
    public sealed record SessionNotFound : SetTitleResult;
}
