using Capacitor.App.Services;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.ViewModels;

/// Not thread-safe: every input and every read belong on the UI thread.
public sealed class AccountChooser {
    readonly Func<string, string, string, string?> _remembered;
    readonly Action<string, string, string, string?> _remember;
    readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);

    string _repoPath = "";
    string _vendor   = "";
    AccountSupport _support;
    bool _fetchFailed;
    AccountsSnapshotDto? _snapshot;
    bool _memoryLoaded;
    bool _picked;
    string? _pickedId;

    public AccountChooser(Func<string, string, string, string?> remembered, Action<string, string, string, string?> remember) {
        _remembered = remembered;
        _remember   = remember;
        Recompute();
    }

    public AccountSelectionState State { get; private set; }
    public bool IsResolved => State is AccountSelectionState.Default or AccountSelectionState.ExplicitValid;
    public string? SelectedAccountId { get; private set; }
    public string? SelectedLabel { get; private set; }
    public bool ShowsSection { get; private set; }
    public IReadOnlyList<AccountChoice> Rows { get; private set; } = [];
    public string? PendingText { get; private set; }
    public bool OffersRetry { get; private set; }
    public string? InvalidReason { get; private set; }

    public event Action? Changed;

    public static string UnavailableReason(string label) => $"{label} is not available on this machine";

    public void EnterTarget(string repoPath, string vendor) {
        _repoPath = repoPath;
        _vendor   = vendor;
        _picked   = false;
        _pickedId = null;
        Recompute();
    }

    public void SetSupport(AccountSupport support, bool fetchFailed) {
        _support     = support;
        _fetchFailed = fetchFailed;
        Recompute();
    }

    public void SetSnapshot(AccountsSnapshotDto? snapshot) {
        _snapshot = snapshot;
        foreach (var account in snapshot?.Accounts ?? []) _labels[account.Id] = account.Label;
        Recompute();
    }

    public void SetMemoryLoaded(bool loaded) {
        _memoryLoaded = loaded;
        Recompute();
    }

    public void Pick(string? accountId) {
        if (OffersAccounts) {
            _picked   = true;
            _pickedId = accountId;
            if (_snapshot is not null) _remember(_snapshot.HostId, _vendor, _repoPath, accountId);
        }

        Recompute();
    }

    public void UseDefault() => Pick(null);

    bool OffersAccounts =>
        string.Equals(_vendor, HarnessId.Claude.VendorId, StringComparison.OrdinalIgnoreCase)
     || string.Equals(_vendor, HarnessId.Codex.VendorId, StringComparison.OrdinalIgnoreCase);

    void Recompute() {
        IReadOnlyList<AccountRow> rows = OffersAccounts && _snapshot is not null ? AccountRows.For(_snapshot, _vendor) : [];

        (State, SelectedAccountId) = Resolve(rows, LiveEntry(rows));

        var label = SelectedAccountId is { } id ? _labels.GetValueOrDefault(id, id) : null;

        SelectedLabel = State switch {
            AccountSelectionState.ExplicitValid   => label,
            AccountSelectionState.ExplicitInvalid => $"{label} — unavailable",
            _                                     => null,
        };
        InvalidReason = State == AccountSelectionState.ExplicitInvalid ? UnavailableReason(label!) : null;
        PendingText   = State == AccountSelectionState.Pending ? "Loading accounts…" : null;
        OffersRetry   = State == AccountSelectionState.Pending && _fetchFailed;
        Rows          = Choices(rows, label);
        ShowsSection  = _support != AccountSupport.Unsupported
            && (State == AccountSelectionState.Pending
             || (_support == AccountSupport.Supported && _snapshot is not null && (Rows.Count >= 2 || State == AccountSelectionState.ExplicitInvalid)));

        Changed?.Invoke();
    }

    /// An entry is read under the snapshot's own host only, so a snapshot of another host can neither
    /// restore it nor drop it. One its own host no longer lists is dropped whatever is selected.
    string? LiveEntry(IReadOnlyList<AccountRow> rows) {
        if (!OffersAccounts || !_memoryLoaded || _snapshot is null) return null;

        var entry = _remembered(_snapshot.HostId, _vendor, _repoPath);
        if (entry is null || Lists(rows, entry)) return entry;

        _remember(_snapshot.HostId, _vendor, _repoPath, null);
        return null;
    }

    (AccountSelectionState, string?) Resolve(IReadOnlyList<AccountRow> rows, string? entry) {
        if (!OffersAccounts) return (AccountSelectionState.Default, null);

        if (_picked) {
            if (_pickedId is null) return (AccountSelectionState.Default, null);

            var valid = _support == AccountSupport.Supported && Lists(rows, _pickedId);
            return (valid ? AccountSelectionState.ExplicitValid : AccountSelectionState.ExplicitInvalid, _pickedId);
        }

        if (!_memoryLoaded || _support == AccountSupport.Unknown || (_support == AccountSupport.Supported && _snapshot is null))
            return (AccountSelectionState.Pending, null);

        if (_support == AccountSupport.Unsupported || entry is null) return (AccountSelectionState.Default, null);

        return (AccountSelectionState.ExplicitValid, entry);
    }

    List<AccountChoice> Choices(IReadOnlyList<AccountRow> rows, string? label) {
        var choices = rows.Select(r => new AccountChoice(r, IsSelected(r), Removed: false)).ToList();

        if (State == AccountSelectionState.ExplicitInvalid && _snapshot is not null)
            choices.Add(new AccountChoice(new AccountRow(SelectedAccountId, _vendor, "", label!, "", false), Selected: true, Removed: true));

        return choices;
    }

    bool IsSelected(AccountRow row) => State switch {
        AccountSelectionState.Default                                                => row.IsDefault,
        AccountSelectionState.ExplicitValid or AccountSelectionState.ExplicitInvalid => row.Id == SelectedAccountId,
        _                                                                            => false,
    };

    static bool Lists(IReadOnlyList<AccountRow> rows, string id) => rows.Any(r => r.Id == id);
}
