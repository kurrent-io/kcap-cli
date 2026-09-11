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
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    internal DateTime CreatedAt { get; }

    readonly ObservableAsPropertyHelper<bool> _isSelected;
    public bool IsSelected => _isSelected.Value;

    readonly ObservableAsPropertyHelper<bool> _needsYou;
    public bool NeedsYou => _needsYou.Value;

    readonly ObservableAsPropertyHelper<bool> _isStale;
    /// A remote row greys out while the lane is stale; a local row is never stale.
    public bool IsStale => _isStale.Value;

    readonly ObservableAsPropertyHelper<string> _statusBadge;
    public string StatusBadge => _statusBadge.Value;

    readonly CompositeDisposable _disposables = new();

    public RailSessionViewModel(
            AgentRow row, IObservable<string?> selectedAgentId,
            IObservable<IReadOnlySet<string>> agentsWithPending, IObservable<bool> remoteStale,
            Action<string> openLocal, Action<string> openRemote) {
        Id = row.Id;
        CreatedAt = row.CreatedAt;
        var kindExtra = row.Kind == "agent" ? null : row.Kind;
        var borrowed = row.WorkLocation == WorkLocationText.Borrowed ? "borrowed" : null;
        var age = UptimeFormat.Format(DateTime.UtcNow - DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));

        Primary = string.IsNullOrEmpty(row.Title) ? null : row.Title;
        HasTitle = Primary is not null;
        Vendor = row.Vendor;
        HasVendor = !string.IsNullOrEmpty(row.Vendor);
        Model = string.IsNullOrEmpty(row.Model) ? null : row.Model;
        HasModel = Model is not null;
        Meta = Join(kindExtra, borrowed, age);
        StatusDot = SessionStatusDots.For(row.Status);
        Tooltip = Join(row.Id, row.Status, SessionStatusDots.WaitsOnUser(row) ? "waiting for input" : null,
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
        _statusBadge = agentsWithPending.Select(set => row.Status == "Failed" || set.Contains(row.Id)
                ? "!" : SessionStatusDots.WaitsOnUser(row) ? "zzz" : "")
            .ToProperty(this, x => x.StatusBadge, initialValue: SessionStatusDots.WaitsOnUser(row) ? "zzz" : "")
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
