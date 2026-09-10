using Capacitor.Cli.Core;
using DynamicData;

namespace Capacitor.App.Services;

public enum PermissionAnswer { Allow, AllowAlways, Deny }

/// Only TransportFailure leaves the request pending; the other two are conclusive.
public enum PermissionResolveKind { Applied, AlreadyDecided, TransportFailure }

public sealed record PermissionResolveOutcome(PermissionResolveKind Kind, string? Error);

public readonly record struct PendingSummary(int Permissions, int Questions, int LocalCount, int ServerCount) {
    public int Total => Permissions + Questions;

    public static PendingSummary From(IEnumerable<PendingPermissionRequest> items) {
        int permissions = 0, questions = 0, local = 0, server = 0;
        foreach (var item in items) {
            if (item.IsQuestion) questions++; else permissions++;
            if (item.Lane == PermissionLane.Local) local++; else server++;
        }
        return new PendingSummary(permissions, questions, local, server);
    }
}

public interface IPermissionService : IDisposable {
    /// Mutated on background continuations — consumers ObserveOn(RxSchedulers.MainThreadScheduler).
    IObservable<IChangeSet<PendingPermissionRequest, string>> Pending { get; }
    /// Replays the current count on subscribe (DynamicData's CountChanged).
    IObservable<int> PendingCount { get; }
    /// The distinct agent ids in the cache; replays the current set on subscribe. A server-lane
    /// item whose session has no agent row yet carries no id and is left out.
    IObservable<IReadOnlySet<string>> AgentsWithPending { get; }
    /// One consistent pair per emission, from a single cache snapshot; replays on subscribe.
    IObservable<PendingSummary> Summary { get; }
    Task<PermissionResolveOutcome> ResolveAsync(PendingPermissionRequest target, PermissionAnswer answer, CancellationToken ct);
    /// Answers a classified AskUserQuestion entry (Questions non-null; ArgumentException otherwise,
    /// as for an invalid answer set — both thrown before anything reaches the wire).
    Task<PermissionResolveOutcome> AnswerAsync(PendingPermissionRequest target, IReadOnlyList<ElicitationAnswer> answers, CancellationToken ct);
    /// Answers an ACP elicitation by option id. Server-lane only: the local permission wire has no
    /// frame for a multi-select or free-text answer, so ArgumentException guards both mismatches.
    Task<PermissionResolveOutcome> AnswerAcpAsync(PendingPermissionRequest target, AcpAnswer answer, CancellationToken ct);
    /// Picks one of an ACP permission's own options. Server-lane only, for the same reason.
    Task<PermissionResolveOutcome> PickOptionAsync(PendingPermissionRequest target, string optionId, CancellationToken ct);
    /// Retires a request whose tool already has a result in the transcript: it was answered where
    /// the daemon cannot see (the vendor's own terminal prompt), so the app is the party that knows.
    /// Concluded here on any ack, a rejected decision from an older daemon included.
    Task<PermissionResolveOutcome> WithdrawAsync(PendingPermissionRequest target, CancellationToken ct);
}
