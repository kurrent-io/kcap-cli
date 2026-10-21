using System.Net.Http.Headers;
using System.Text;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// Queues events and ships them to PostHog's <c>/batch/</c> endpoint under a wall-clock budget.
/// A flush that fails for any reason spills to the spool instead of retrying inline — the
/// caller is on a user's command path and must not wait on a broken network.
/// </summary>
/// <param name="consent">The saved choice, asked before a send or a spill, for a process that outlives an opt-out
/// saved by another: false withdraws the send and drops what is queued and spooled; null, a choice that cannot be
/// read, sends and spools nothing but leaves the spool. Omitted for a process that never needs it.</param>
public sealed class TelemetryClient(
        HttpMessageHandler handler, TelemetrySpool spool, string token, string endpoint,
        TimeProvider timeProvider, Func<bool?>? consent = null) : ITelemetrySink {
    readonly List<TelemetryEvent> _queue = [];
    readonly Lock                 _flushLock = new();
    Task<bool>                    _lastFlush = Task.FromResult(true);
    readonly Lock                    _discardLock = new();
    volatile bool                    _discarded;
    CancellationTokenSource?         _sending;

    readonly TimeProvider _clock = timeProvider;

    public void Enqueue(TelemetryEvent e) {
        lock (_queue) _queue.Add(e);
    }

    /// <summary>
    /// The caller is turning telemetry off: drops the queue and the spool, and stops any flush still
    /// waiting its turn from sending or spilling. A request already on the wire cannot be recalled,
    /// but if it fails its events are dropped rather than parked for a later run to send.
    /// </summary>
    public void Discard() => Drop(latch: true);

    public void Abandon() => Drop(latch: true, clearSpool: false);

    /// <summary>
    /// A consent another process withdrew: what is queued or spooled goes, but the client is not shut, so
    /// events captured after the choice goes back on are sent as usual.
    /// </summary>
    void Withdraw() => Drop(latch: false);

    void Drop(bool latch, bool clearSpool = true) {
        lock (_discardLock) {
            if (latch) _discarded = true;
            // First, and inside the lock: a send registered but not yet begun then fails its token check
            // instead of going out. The flush unregisters under this lock before disposing, so the
            // source is live here; its callbacks never take this lock.
            try { _sending?.Cancel(); } catch (ObjectDisposedException) { }
            lock (_queue) _queue.Clear();
            if (clearSpool) try { spool.Clear(); } catch { }
        }
    }

    // Under the lock, so a spill can never land after the discard cleared the spool. A consent withdrawn
    // since the capture keeps the events off the disk as well as off the wire; one that cannot be read keeps
    // these events off it but leaves the spool to the next reader.
    void Spill(List<TelemetryEvent> queued) {
        lock (_discardLock) {
            if (_discarded) return;
            switch (consent?.Invoke()) {
                case false:
                    Withdraw();
                    return;
                case null when consent is not null:
                    return;
            }
            spool.Append(queued);
        }
    }

    /// <summary>Ships queued + previously spooled events. Returns false when nothing reached
    /// PostHog, in which case everything has been spooled for a later attempt.
    ///
    /// <paramref name="budget"/> bounds this WHOLE call, not just the HTTP phase: timing starts
    /// here, before <see cref="TelemetrySpool.DrainAll"/> (disk I/O, up to 2000 spooled lines)
    /// and <see cref="PostHogPayload.Build"/> (serializing the whole batch) — both of which run
    /// synchronously on the caller's command path (<c>CliTelemetry.CaptureNow</c>, the
    /// ProcessExit handler). Without that, a slow disk with a large spool could stall a command
    /// well past the intended budget before the HTTP phase even started timing.
    ///
    /// Flushes run one at a time: each drains, appends to and clears the same spool, so an overlapping
    /// one could clear events another had just spilled. Waiting for an earlier flush counts against
    /// the budget, and a flush whose wait used it all spills its queue without draining the spool.</summary>
    public Task<bool> FlushAsync(string distinctId, string? orgGroup, TimeSpan budget) {
        var start = _clock.GetTimestamp();

        lock (_flushLock) {
            var prior = _lastFlush;
            return _lastFlush = FlushAfterAsync(prior, start, distinctId, orgGroup, budget);
        }
    }

    async Task<bool> FlushAfterAsync(Task<bool> prior, long start, string distinctId, string? orgGroup, TimeSpan budget) {
        try {
            await prior;
        } catch {
            // An earlier flush's failure is its own; this one still runs and must not throw.
        }

        if (_discarded) return false;

        List<TelemetryEvent> queued;
        lock (_queue) {
            queued = [.. _queue];
            _queue.Clear();
        }

        if (_clock.GetElapsedTime(start) >= budget) {
            Spill(queued);
            return false;
        }

        try {
            // Drained OUTSIDE the lock: it is file I/O, and blocking a concurrent Enqueue on disk
            // is not what the queue lock is for. Moved inside try as belt-and-braces: even if
            // TelemetrySpool.DrainAll now catches broadly, a pathological config path can still
            // escape a third category of exception, and we must never throw to the NativeAOT runtime.
            //
            // Spooled and queued events are kept apart deliberately. DrainAll is a read, not a take,
            // so re-appending the spooled ones on failure would duplicate them on every retry, and
            // the eventual success would ship the duplicates. Only the queued ones need spilling.
            var spooled = spool.DrainAll();
            var pending = new List<TelemetryEvent>(spooled.Count + queued.Count);
            pending.AddRange(spooled);   // spool first: previously-failed events keep their place in the funnel
            pending.AddRange(queued);

            if (pending.Count == 0) return true;

            var body = PostHogPayload.Build(pending, token, distinctId, orgGroup);

            // Whatever the drain + build above already spent comes out of the budget before the
            // HTTP phase gets any of it. If that alone exhausted it, a request could not finish
            // in time regardless of how it's timed out — spill instead of starting one that has
            // no chance of completing within budget.
            var remaining = budget - _clock.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero) {
                Spill(queued);
                return false;
            }

            using var http = new HttpClient(handler, disposeHandler: false) { Timeout = remaining };
            using var cts  = new CancellationTokenSource(remaining, _clock);
            using var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            // Re-checked after the drain and build, under the lock Discard takes: an opt-out before this
            // point starts no send, and one after it cancels the send in flight.
            var saved     = consent?.Invoke();
            var withdrawn = saved == false;
            if (consent is not null && saved is null) {
                Spill(queued);
                return false;
            }
            lock (_discardLock) {
                if (_discarded) return false;
                if (!withdrawn) _sending = cts;
            }

            if (withdrawn) {
                Withdraw();
                return false;
            }

            HttpResponseMessage response;
            try {
                response = await http.PostAsync($"{endpoint.TrimEnd('/')}/batch/", content, cts.Token);
            } finally {
                lock (_discardLock) _sending = null;
            }

            if (!response.IsSuccessStatusCode) {
                Spill(queued);
                return false;
            }

            spool.Clear();
            return true;
        } catch {
            // Telemetry code must NEVER throw: under NativeAOT this becomes SIGABRT. Any exception —
            // whether from draining the spool, building the payload, setting the budget, network/serialization errors, or
            // anything else — spills the queued events for replay, exactly like a failed POST.
            Spill(queued);
            return false;
        }
    }
}
