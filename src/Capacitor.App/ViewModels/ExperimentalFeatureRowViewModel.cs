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

    internal ExperimentalFeatureRowViewModel(ExperimentalFeature feature, bool enabled, bool canChange, Func<ExperimentalFeatureRowViewModel, bool, Task> save) {
        _feature = feature;
        _enabled = enabled;
        _save = save;
        CanChange = canChange;
        Recompute();
    }

    internal string Id => _feature.Id;
    public string DisplayName => _feature.DisplayName;
    public string Description => _feature.Description;
    public bool CanChange { get; }

    public bool IsEnabled {
        get => _enabled;
        set {
            if (!CanChange || value == _enabled) return;
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

    internal void Revert(bool value) {
        this.RaiseAndSetIfChanged(ref _enabled, value, nameof(IsEnabled));
        Recompute();
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
            ExperimentalFeatureStateKind.AppliedLocally => "Applied; the server has not been told yet",
            ExperimentalFeatureStateKind.Unknown => "This daemon is too old to apply it. Update kcap and restart the daemon",
            ExperimentalFeatureStateKind.DaemonNotRunning => "Saved; applies when the daemon starts",
            _ => "",
        };
        StatusIsWarning = kind is ExperimentalFeatureStateKind.OnNotActive or ExperimentalFeatureStateKind.Unknown;
    }
}
