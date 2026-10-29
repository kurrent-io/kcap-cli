using ReactiveUI.Reactive;

namespace Capacitor.App.Services.LocalSetup;

/// What a run's `kcap local upgrade --json` lines have said so far. The outcome is decided afterwards
/// by <see cref="LocalUpgradeOutcome.Settle"/>; unknown events are ignored.
public sealed class LocalUpgradeProgress : ReactiveObject {
    long _bytes;
    // Between `recovering` and `recovered` the lines are an earlier, interrupted run's.
    bool _recovering;
    LocalUpgradePhase _phase;
    double? _downloadFraction;
    string? _from, _to, _switchedTo;

    public LocalUpgradePhase Phase {
        get => _phase;
        private set => this.RaiseAndSetIfChanged(ref _phase, value);
    }

    public double? DownloadFraction {
        get => _downloadFraction;
        private set => this.RaiseAndSetIfChanged(ref _downloadFraction, value);
    }

    /// The stack's version before the upgrade.
    public string? From {
        get => _from;
        private set => this.RaiseAndSetIfChanged(ref _from, value);
    }

    /// The version the upgrade moves the stack to.
    public string? To {
        get => _to;
        private set => this.RaiseAndSetIfChanged(ref _to, value);
    }

    /// Set once the registry names the new version, whatever happens after.
    public string? SwitchedTo {
        get => _switchedTo;
        private set => this.RaiseAndSetIfChanged(ref _switchedTo, value);
    }

    public string? ServerUrl { get; private set; }

    public string? SkipReason { get; private set; }

    public string? FailedCode { get; private set; }

    public string? FailedMessage { get; private set; }

    public bool? FailedSwitched { get; private set; }

    public void Apply(LocalSetupLine line) {
        var root = line.Root;
        switch (line.Event) {
            case "recovering":
                _recovering = true;
                Phase       = LocalUpgradePhase.Recovering;
                return;
            case "recovered":
                _recovering = false;
                Phase       = LocalUpgradePhase.Waiting;
                return;
        }
        if (_recovering && line.Event is not "failed") return;

        switch (line.Event) {
            case "upgrade_planned":
                From = root.Str("from_version") ?? From;
                To   = root.Str("version") ?? To;
                break;
            case "downloading":
                _bytes           = root.Num("bytes") ?? 0;
                Phase            = LocalUpgradePhase.Downloading;
                DownloadFraction = Fraction(0);
                break;
            case "download_progress":
                if (root.Num("bytes") is > 0 and var total) _bytes = total;
                Phase            = LocalUpgradePhase.Downloading;
                DownloadFraction = Fraction(root.Num("received") ?? 0);
                break;
            case "stopping":
                Phase = LocalUpgradePhase.Stopping;
                break;
            case "switched":
                SwitchedTo = root.Str("version") ?? To;
                break;
            case "starting":
                Phase = LocalUpgradePhase.Starting;
                break;
            case "restarting":
                Phase = LocalUpgradePhase.Restarting;
                break;
            case "ready":
                ServerUrl = root.Str("server_url") ?? ServerUrl;
                break;
            case "upgraded":
                From      = root.Str("from_version") ?? From;
                To        = root.Str("version") ?? To;
                ServerUrl = root.Str("server_url") ?? ServerUrl;
                break;
            case "nothing_to_do":
                SkipReason = root.Str("reason");
                To         = root.Str("version") ?? To;
                From       = root.Str("installed_version") ?? From;
                break;
            case "failed":
                FailedCode     = root.Str("code");
                FailedMessage  = root.Str("message");
                FailedSwitched = root.Bool("switched");
                break;
        }
    }

    double? Fraction(long received) => _bytes > 0 ? Math.Clamp((double)received / _bytes, 0, 1) : null;
}
