using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// One connection generation's daemon listing. Daemons is null until that generation answers, and
/// null is not an empty listing; FetchFailed marks a failed fetch, which leaves it unanswered.
public sealed record DaemonListing(IReadOnlyList<DaemonInfo>? Daemons, int Generation, bool FetchFailed);
