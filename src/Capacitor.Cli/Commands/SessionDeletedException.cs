using System.Net;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The server answered a session's probe with 410: its owner deleted it there. Import counts the session
/// as <see cref="ImportCommand.ClassificationStatus.Deleted"/> and sends nothing for it. Anyone else gets
/// the 404 an absent session gets, so only the owner's import can tell it apart from a new one.
/// </summary>
internal sealed class SessionDeletedException(string sessionId)
    : Exception($"session {sessionId} was deleted on the server") {
    public string SessionId { get; } = sessionId;

    internal static bool IsDeleted(HttpResponseMessage response) => response.StatusCode is HttpStatusCode.Gone;

    internal static void ThrowIfDeleted(HttpResponseMessage response, string sessionId) {
        if (IsDeleted(response)) throw new SessionDeletedException(sessionId);
    }
}
