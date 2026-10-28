using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One experimental feature's switch and the line under it. The switch saves on change; a save the store
/// refuses puts it back.
public sealed class ExperimentalFeatureRowViewModel : ReactiveObject {
    readonly ExperimentalFeature _feature;
    readonly Func<ExperimentalFeatureRowViewModel, bool, Task> _save;
    bool _enabled;
    bool _daemonRunning;
    bool _hasStatus;
    DaemonStatusDto? _status;
    string _statusText = "";
    bool _statusIsWarning;
    bool _canChange;
    bool _changedHere;

    internal ExperimentalFeatureRowViewModel(ExperimentalFeature feature, bool enabled, bool canChange, Func<ExperimentalFeatureRowViewModel, bool, Task> save) {
        _feature = feature;
        _enabled = enabled;
        _save = save;
        _canChange = canChange;
        Recompute();
    }

    /// The daemon routes on what the server last accepted, so Codex keeps the previous setting until a
    /// registration carries the change; the next connection does that unprompted.
    public const string AppliedLocallyText =
        "Saved. Codex keeps the previous setting until the daemon next connects to your kcap server.";
    /// A daemon starts with nothing accepted, so a switch left on waits for that first registration.
    public const string WaitingForServerText = "Starts once the daemon connects to your kcap server.";

    internal string Id => _feature.Id;
    public string DisplayName => _feature.DisplayName;
    public string Description => _feature.Description;
    public bool CanChange {
        get => _canChange;
        private set => this.RaiseAndSetIfChanged(ref _canChange, value);
    }

    public bool IsEnabled {
        get => _enabled;
        set {
            if (!CanChange || value == _enabled) return;
            _changedHere = true;
            this.RaiseAndSetIfChanged(ref _enabled, value);
            Recompute();
            _ = _save(this, value);
        }
    }

    public string StatusText {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// Only a switch that is on and not working, or a daemon that cannot report, is attention.
    public bool StatusIsWarning {
        get => _statusIsWarning;
        private set => this.RaiseAndSetIfChanged(ref _statusIsWarning, value);
    }

    /// <param name="hasStatus">False while the daemon's state is still being learned, which shows nothing.</param>
    internal void Observe(bool daemonRunning, bool hasStatus, DaemonStatusDto? status) {
        _daemonRunning = daemonRunning;
        _hasStatus = hasStatus;
        _status = status;
        Recompute();
    }

    /// A failed save puts the old value back, so the switch no longer counts as changed here.
    internal void Revert(bool value) {
        _changedHere = false;
        Show(value);
    }

    void Show(bool value) {
        this.RaiseAndSetIfChanged(ref _enabled, value, nameof(IsEnabled));
        Recompute();
    }

    /// Takes the saved value without saving it back.
    internal void Load(bool enabled, bool canChange) {
        CanChange = canChange;
        // Reading back this row's own save is no news; a different value was set elsewhere.
        if (enabled != _enabled) _changedHere = false;
        Show(enabled);
    }

    void Recompute() {
        if (!_hasStatus) {
            StatusText = "";
            StatusIsWarning = false;
            return;
        }
        var (kind, reason) = ExperimentalFeatureState.Classify(_enabled, _daemonRunning, _status, _feature.Id);
        StatusText = kind switch {
            ExperimentalFeatureStateKind.Active => "Active",
            ExperimentalFeatureStateKind.OnNotActive => $"On, not active: {reason}",
            ExperimentalFeatureStateKind.Applying => "Applying…",
            // Unprompted, an unregistered daemon is the Daemon page's news; a switch left off changes nothing.
            ExperimentalFeatureStateKind.AppliedLocally => _changedHere ? AppliedLocallyText : _enabled ? WaitingForServerText : "",
            ExperimentalFeatureStateKind.Unknown => "The daemon runs an older version of kcap. Update kcap. The daemon then restarts on its own.",
            ExperimentalFeatureStateKind.DaemonNotRunning when _enabled => "Saved. Applies when the daemon starts.",
            _ => "",
        };
        StatusIsWarning = kind is ExperimentalFeatureStateKind.OnNotActive or ExperimentalFeatureStateKind.Unknown;
    }
}
