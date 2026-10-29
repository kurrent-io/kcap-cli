namespace Capacitor.App.Services;

/// One pending retry on a bounded backoff. Not thread-safe: the owner calls every member under its
/// own lock, the fired callback's Claim included. Cancelling bumps the ticket, so a callback that
/// lost the race with its own cancellation fails Claim and does nothing.
internal sealed class RetryBackoff(TimeProvider time, TimeSpan[] delays) {
    ITimer? _timer;
    long _ticket;
    int _attempt;

    public void Schedule(Action<long> fire) {
        Cancel();
        var delay = delays[Math.Min(_attempt++, delays.Length - 1)];
        var ticket = _ticket;
        _timer = time.CreateTimer(_ => fire(ticket), null, delay, Timeout.InfiniteTimeSpan);
    }

    public void Reset() {
        _attempt = 0;
        Cancel();
    }

    public bool Claim(long ticket) {
        if (ticket != _ticket) return false;
        Cancel();
        return true;
    }

    void Cancel() {
        _ticket++;
        _timer?.Dispose();
        _timer = null;
    }
}
