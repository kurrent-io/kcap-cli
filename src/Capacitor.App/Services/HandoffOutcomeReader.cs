using System.Reactive.Linq;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Follows the daemon's stored hand-off outcome for a session id. The daemon keeps it by the
/// successor's id, so the answer is the same whether the view is the live agent or the server row
/// the successor became.
public sealed class HandoffOutcomeReader(IDaemonClientService daemon, ILocalControlOps ops, TimeProvider time) {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan NullWindow = TimeSpan.FromMinutes(2);

    /// Emits each outcome that differs in value from the last: every read deserializes fresh lists,
    /// so reference equality would re-emit each poll. An answer of null (nothing stored yet, or a
    /// failed read) is retried for <see cref="NullWindow"/>; a running outcome is followed without limit,
    /// and so is an interrupted one, which the daemon reruns when it resumes the successor.
    /// Completes on a terminal state. Each connect of a daemon with handoff/1 starts the reads again,
    /// window included, so a takeover the daemon resumes after a long absence is still found.
    public IObservable<HandoffOutcomeDto> Watch(string sessionId) =>
        Observable.Defer(() => {
            HandoffOutcomeDto? last = null;
            var polls = daemon.Status
                .Select(status => status.State == AttachState.Connected && status.Capabilities?.Contains(HandoffWire.Capability) == true)
                .DistinctUntilChanged()
                .Where(capable => capable)
                .Select(_ => Poll(sessionId));
            return Observable.Switch(polls)
                .Where(outcome => {
                    var fresh = !Same(outcome, last);
                    last = outcome;
                    return fresh;
                })
                .TakeUntil(outcome => outcome.State is not ("running" or "interrupted"));
        });

    /// The chat card's stream for one session id, inside a Switch over ids: it opens with null so a
    /// card from the previous id is cleared rather than carried over.
    public static IObservable<HandoffOutcomeDto?> CardFor(HandoffOutcomeReader reader, string? sessionId) =>
        sessionId is null
            ? Observable.Return<HandoffOutcomeDto?>(null)
            : reader.Watch(sessionId).Select(outcome => (HandoffOutcomeDto?)outcome).StartWith((HandoffOutcomeDto?)null);

    IObservable<HandoffOutcomeDto> Poll(string sessionId) =>
        Observable.Create<HandoffOutcomeDto>(async (observer, ct) => {
            var started = time.GetTimestamp();
            var seen = false;
            HandoffOutcomeDto? last = null;
            try {
                while (!ct.IsCancellationRequested) {
                    var outcome = await Task.Run(() => ops.ReadHandoffOutcomeAsync(sessionId, ct), ct).ConfigureAwait(false);
                    if (outcome is not null) {
                        seen = true;
                        if (!Same(outcome, last)) observer.OnNext(outcome);
                        last = outcome;
                        if (outcome.State is not ("running" or "interrupted")) break;
                    } else if (!seen && time.GetElapsedTime(started) >= NullWindow) {
                        break;
                    }
                    await Task.Delay(PollInterval, time, ct).ConfigureAwait(false);
                }
                observer.OnCompleted();
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            } catch (Exception ex) {
                observer.OnError(ex);
            }
        });

    internal static bool Same(HandoffOutcomeDto a, HandoffOutcomeDto? b) =>
        b is not null && a.SourceSessionId == b.SourceSessionId && a.SourceTitle == b.SourceTitle
        && a.State == b.State && a.Detail == b.Detail && a.Items.SequenceEqual(b.Items);
}
