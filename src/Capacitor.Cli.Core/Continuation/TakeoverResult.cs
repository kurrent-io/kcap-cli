using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Continuation;

public abstract record TakeoverResult {
    TakeoverResult() { }

    /// <summary>Nothing was written: the session may still be running, or cannot be continued.</summary>
    public sealed record Refused(string Reason) : TakeoverResult;

    public sealed record Unauthorized : TakeoverResult;

    /// <summary>The previous session could not be read, so nothing was attempted.</summary>
    public sealed record Failed(string Reason) : TakeoverResult;

    /// <summary><paramref name="Unsuccessful"/>: work items, plans or claims were not fully carried over.
    /// <paramref name="CredentialRejected"/>: a write was rejected with 401, so the caller should repair the login.</summary>
    public sealed record Completed(JsonObject Outcome, bool Unsuccessful, bool CredentialRejected = false) : TakeoverResult;

    /// <summary>The run stopped part way and attempted no write after that point. An entry whose
    /// <c>attached</c> is null was in flight or never attempted, so its outcome is unknown.</summary>
    public sealed record Interrupted(JsonObject Outcome, string Code) : TakeoverResult {
        public const string Cancelled = "cancelled";

        /// <summary>The server's fence refused a write because this takeover no longer holds the source.</summary>
        public const string ReservationLost = "source_reservation_lost";
    }
}
