using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;

namespace Capacitor.App.Services;

/// Labels a link to one of the app server's agent pages with that agent's title, read from the rows
/// the app already holds. The title is read when the link is drawn; <see cref="Titled"/> announces
/// an agent whose title has just become known, so a link drawn before it registered can fill in.
/// Nothing announces a later rename or status change.
public sealed class AgentLinkLabels : IDisposable {
    readonly string? _serverUrl;
    readonly IObservableCache<AgentRow, string> _rows;
    readonly Subject<string> _titled = new();
    readonly IDisposable _subscription;

    public AgentLinkLabels(string? serverUrl, IObservableCache<AgentRow, string> rows, IScheduler scheduler) {
        _serverUrl = serverUrl;
        _rows = rows;
        _subscription = rows.Connect()
            .SelectMany(changes => changes.Where(BecameTitled).Select(change => AgentIds.Normalize(change.Current.Id)).OfType<string>())
            .ObserveOn(scheduler)
            .Subscribe(_titled);
    }

    /// Normalized agent ids, on the scheduler the labels were built with.
    public IObservable<string> Titled => _titled;

    /// Null for any link that is not to an agent page on the app's server.
    public AgentLinkLabel? For(string url) {
        if (AgentLinkOpener.AgentIdOf(url, _serverUrl) is not { } raw || AgentIds.Normalize(raw) is not { } id) return null;
        var title = TitleOf(raw) ?? TitleOf(id) ?? TitleOfAnySpelling(id);
        return new AgentLinkLabel(id, title ?? $"agent {(id.Length > 8 ? id[..8] : id)}", title is not null);
    }

    string? TitleOf(string agentId) {
        foreach (var lane in Lanes)
            if (_rows.Lookup($"{lane}:{agentId}") is { HasValue: true, Value: var row } && HasTitle(row))
                return row.Title;
        return null;
    }

    // A row keeps the spelling its source used, so a dashed GUID key never matches an undashed link.
    string? TitleOfAnySpelling(string normalizedId) =>
        _rows.Items.FirstOrDefault(row => IsAgentLane(row) && HasTitle(row) && AgentIds.Normalize(row.Id) == normalizedId)?.Title;

    static bool IsAgentLane(AgentRow row) => row.Origin is AgentOrigin.Local or AgentOrigin.Pending or AgentOrigin.Remote;

    // Server rows are keyed by session id, which an agent link does not carry.
    static readonly string[] Lanes = ["local", "pending", "remote"];

    static bool BecameTitled(Change<AgentRow, string> change) =>
        IsAgentLane(change.Current)
        && HasTitle(change.Current)
        && change.Reason switch {
            ChangeReason.Add => true,
            ChangeReason.Update => !(change.Previous is { HasValue: true, Value: var previous } && HasTitle(previous)),
            _ => false,
        };

    static bool HasTitle(AgentRow row) => !string.IsNullOrWhiteSpace(row.Title);

    public void Dispose() {
        _subscription.Dispose();
        _titled.Dispose();
    }
}
