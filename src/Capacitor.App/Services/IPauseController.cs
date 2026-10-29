namespace Capacitor.App.Services;

public sealed record PauseState(bool Checked, bool Verified, bool Busy);

/// State is replay-1,
/// seeded with (Checked: false, Verified: false, Busy: false) — unverified until the first
/// successful refresh.
public interface IPauseController {
    IObservable<PauseState> State { get; }
    void RequestRefresh();            // passive; DROPPED while the lane is busy
    void RequestToggle(bool desired); // desired checked value, single-flight + one queued slot
}
