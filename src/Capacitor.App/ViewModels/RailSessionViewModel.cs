using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One session row of the rail. Recreated per row revision (DynamicData Transform), so every
/// static field is computed once from the ctor row; IsSelected and NeedsYou stay live because
/// selection and pending-set membership each change without a row revision. Age is a
/// point-in-time snapshot (SessionCardViewModel precedent).
public sealed class RailSessionViewModel : ReactiveObject, IDisposable {
    public string Id { get; }
    /// Null when the row has no title, in which case the chips line stands alone as the row.
    public string? Primary { get; }
    public bool HasTitle { get; }
    public string Vendor { get; }
    public bool HasVendor { get; }
    public string? Model { get; }
    public bool HasModel { get; }
    public string Meta { get; }
    public IBrush StatusDot { get; }
    public string Tooltip { get; }
    /// The daemon name badge for a remote row; null for a local one.
    public string? MachineBadge { get; }
    public bool IsRemote { get; }
    /// A launch the daemon has not published yet: Meta carries its stage instead of an age.
    public bool IsStarting { get; }
    /// The status dot pulses while the launch is starting or while the daemon counts subagents
    /// running under the session.
    public bool DotPulses { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    internal DateTime CreatedAt { get; }

    readonly ObservableAsPropertyHelper<bool> _isSelected;
    public bool IsSelected => _isSelected.Value;

    readonly ObservableAsPropertyHelper<bool> _needsYou;
    public bool NeedsYou => _needsYou.Value;

    readonly ObservableAsPropertyHelper<bool> _showsStatusDot;
    /// The badge replaces the dot for a row that needs attention, except that ongoing subagent
    /// work keeps the pulsing dot beside it.
    public bool ShowsStatusDot => _showsStatusDot.Value;

    readonly ObservableAsPropertyHelper<bool> _isStale;
    /// A remote row greys out while the lane is stale; a local row is never stale.
    public bool IsStale => _isStale.Value;

    readonly ObservableAsPropertyHelper<bool> _showsIdleBadge;
    /// The badge is a clock for a finished turn; a failure or a pending permission takes "!" instead.
    public bool ShowsIdleBadge => _showsIdleBadge.Value;

    readonly CompositeDisposable _disposables = new();

    public RailSessionViewModel(
            AgentRow row, IObservable<string?> selectedAgentId,
            IObservable<IReadOnlySet<string>> agentsWithPending, IObservable<bool> remoteStale,
            Action<string> openLocal, Action<string> openRemote, TimeProvider time) {
        Id = row.Id;
        CreatedAt = row.CreatedAt;
        var kindExtra = row.Kind == "agent" ? null : row.Kind;
        var borrowed = row.WorkLocation == WorkLocationText.Borrowed ? "borrowed" : null;
        var age = UptimeFormat.Format(
            time.GetUtcNow().UtcDateTime - DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));

        Primary = string.IsNullOrEmpty(row.Title) ? null : row.Title;
        HasTitle = Primary is not null;
        Vendor = row.Vendor;
        HasVendor = !string.IsNullOrEmpty(row.Vendor);
        Model = string.IsNullOrEmpty(row.Model) ? null : row.Model;
        HasModel = Model is not null;
        IsStarting = row.Origin == AgentOrigin.Pending;
        var subagents = row.LiveSubagents is int live and > 0 ? $"{live} subagent{(live == 1 ? "" : "s")}" : null;
        DotPulses = IsStarting || subagents is not null;
        Meta = IsStarting ? LaunchStages.Label(row.LaunchStage) : Join(kindExtra, borrowed, age, subagents);
        StatusDot = SessionStatusDots.For(row);
        Tooltip = IsStarting
            ? Join(row.Id, "Starting", LaunchStages.Label(row.LaunchStage))
            : Join(row.Id, row.Status, SessionStatusDots.WaitsOnUser(row) ? "waiting for input" : null,
                subagents is null ? null : $"{subagents} running",
                row.RequesterDisplay, row.BorrowedFrom is null ? null : $"borrowed {row.BorrowedFrom}");
        MachineBadge = row.MachineBadge;
        IsRemote = row.Origin == AgentOrigin.Remote;

        _isSelected = selectedAgentId.Select(sel => sel == row.Id)
            .ToProperty(this, x => x.IsSelected, initialValue: false)
            .DisposeWith(_disposables);

        var byStatus = SessionStatusDots.NeedsAttention(row);
        _needsYou = agentsWithPending.Select(set => byStatus || set.Contains(row.Id))
            .ToProperty(this, x => x.NeedsYou, initialValue: byStatus)
            .DisposeWith(_disposables);
        _showsStatusDot = agentsWithPending.Select(set => !(byStatus || set.Contains(row.Id)) || DotPulses)
            .ToProperty(this, x => x.ShowsStatusDot, initialValue: !byStatus || DotPulses)
            .DisposeWith(_disposables);
        _showsIdleBadge = agentsWithPending.Select(set =>
                SessionStatusDots.WaitsOnUser(row) && row.Status != "Failed" && !set.Contains(row.Id))
            .ToProperty(this, x => x.ShowsIdleBadge, initialValue: SessionStatusDots.WaitsOnUser(row) && row.Status != "Failed")
            .DisposeWith(_disposables);

        _isStale = (IsRemote ? remoteStale : Observable.Return(false))
            .ToProperty(this, x => x.IsStale, initialValue: false)
            .DisposeWith(_disposables);

        OpenCommand = ReactiveCommand.Create(() => (IsRemote ? openRemote : openLocal)(row.Id));
        _disposables.Add(OpenCommand);
    }

    static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    public void Dispose() => _disposables.Dispose();
}
