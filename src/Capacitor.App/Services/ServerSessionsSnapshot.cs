using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// One read's result in full: a consumer that merged the three sets one at a time would act on
/// a live row whose owner it has not yet been told about. PinnedSessionId is set while a pin is
/// held; Pinned is its row only while no listing supplies the session.
public sealed record ServerSessionsSnapshot(
        IReadOnlyList<RepoSessionDto> Live, IReadOnlyList<RepoSessionDto> MineLive, IReadOnlyList<RepoSessionDto> Ended,
        RepoSessionDto? Pinned = null, string? PinnedSessionId = null) {
    public static readonly ServerSessionsSnapshot Empty = new([], [], []);
}
