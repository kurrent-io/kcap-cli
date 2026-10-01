using System.Text.Json.Nodes;

namespace Capacitor.Cli.Continuation;

abstract record TakeoverResult {
    TakeoverResult() { }

    /// <summary>Nothing was written: the session may still be running, or cannot be continued.</summary>
    public sealed record Refused(string Reason) : TakeoverResult;

    public sealed record Unauthorized : TakeoverResult;

    /// <summary>The previous session could not be read, so nothing was attempted.</summary>
    public sealed record Failed(string Reason) : TakeoverResult;

    /// <summary><paramref name="Unsuccessful"/>: a write or a read failed, so not everything was carried over.</summary>
    public sealed record Completed(JsonObject Outcome, bool Unsuccessful) : TakeoverResult;
}
