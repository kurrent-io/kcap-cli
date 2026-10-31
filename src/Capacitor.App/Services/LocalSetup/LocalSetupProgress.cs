using ReactiveUI.Reactive;

namespace Capacitor.App.Services.LocalSetup;

/// The rows and flags a run's JSON lines drive. Lines only move this forward; the outcome is decided
/// afterwards by <see cref="LocalInstallOutcome.Settle"/>. Unknown events are ignored.
public sealed class LocalSetupProgress(LocalEmbeddingChoice? embeddings = null) : ReactiveObject {
    long _bytes;
    double? _downloadFraction;
    bool _recovering, _rollingBack, _reused, _sawSuccess, _sawRecovery, _serviceDeclined, _serviceUnavailable;
    string? _serverUrl, _failedCode, _failedMessage;

    public InstallRow Download   { get; } = new();
    public InstallRow Verify     { get; } = new();
    public InstallRow Install    { get; } = new();
    public InstallRow FirstStart { get; } = new();

    public LocalEmbeddingProgress Embeddings { get; } = new(embeddings);

    public double? DownloadFraction {
        get => _downloadFraction;
        private set => this.RaiseAndSetIfChanged(ref _downloadFraction, value);
    }

    public bool Recovering {
        get => _recovering;
        private set => this.RaiseAndSetIfChanged(ref _recovering, value);
    }

    public bool RollingBack {
        get => _rollingBack;
        private set => this.RaiseAndSetIfChanged(ref _rollingBack, value);
    }

    public bool Reused {
        get => _reused;
        private set {
            this.RaiseAndSetIfChanged(ref _reused, value);
            this.RaisePropertyChanged(nameof(Plan));
        }
    }

    public string? ServerUrl {
        get => _serverUrl;
        private set => this.RaiseAndSetIfChanged(ref _serverUrl, value);
    }

    public bool SawSuccess {
        get => _sawSuccess;
        private set {
            this.RaiseAndSetIfChanged(ref _sawSuccess, value);
            this.RaisePropertyChanged(nameof(Plan));
        }
    }

    /// The administrator prompt for the Windows service was declined; the server runs while the user is signed in.
    public bool WindowsServiceDeclined {
        get => _serviceDeclined;
        private set => this.RaiseAndSetIfChanged(ref _serviceDeclined, value);
    }

    /// The Windows service could not be installed; the server runs while the user is signed in.
    public bool WindowsServiceUnavailable {
        get => _serviceUnavailable;
        private set => this.RaiseAndSetIfChanged(ref _serviceUnavailable, value);
    }

    public string? FailedCode {
        get => _failedCode;
        private set => this.RaiseAndSetIfChanged(ref _failedCode, value);
    }

    public string? FailedMessage {
        get => _failedMessage;
        private set => this.RaiseAndSetIfChanged(ref _failedMessage, value);
    }

    /// `reused`, `recovered` (an earlier setup was finished and this one committed) or `fresh`.
    public string Plan => Reused ? "reused" : _sawRecovery && SawSuccess ? "recovered" : "fresh";

    public void Apply(LocalSetupLine line) {
        if (Embeddings.Apply(line)) {
            FinishInstall();
            return;
        }

        var root = line.Root;
        switch (line.Event) {
            case "recovering":
                _sawRecovery = true;
                Recovering   = true;
                break;
            case "recovered":
                _sawRecovery = true;
                Recovering   = false;
                break;
            case "downloading":
                _bytes           = root.Num("bytes") ?? 0;
                Download.State   = RowState.Running;
                DownloadFraction = Fraction(0);
                break;
            case "download_progress":
                if (root.Num("bytes") is > 0 and var total) _bytes = total;
                Download.State   = RowState.Running;
                DownloadFraction = Fraction(root.Num("received") ?? 0);
                break;
            case "verified" or "unpacked":
                Download.State = RowState.Done;
                Verify.State   = RowState.Done;
                StartInstall();
                break;
            // Not port_chosen: the CLI picks the port before it downloads anything.
            case "starting" or "committing":
                StartFirstStart();
                break;
            case "ready":
                StartFirstStart();
                ServerUrl = root.Str("server_url") ?? ServerUrl;
                break;
            case "reused":
                Reused    = true;
                ServerUrl = root.Str("server_url") ?? ServerUrl;
                AllDone();
                SawSuccess = true;
                break;
            case "committed":
                ServerUrl = root.Str("server_url") ?? ServerUrl;
                AllDone();
                SawSuccess = true;
                break;
            case "windows_service_declined":
                WindowsServiceDeclined = true;
                break;
            case "windows_service_unavailable":
                WindowsServiceUnavailable = true;
                break;
            case "rolling_back":
                RollingBack = true;
                break;
            case "rolled_back":
                RollingBack = false;
                break;
            case "failed":
                FailedCode    = root.Str("code");
                FailedMessage = root.Str("message");
                break;
        }
        this.RaisePropertyChanged(nameof(Plan));
    }

    /// A run that settled without success leaves nothing in flight.
    public void Halt() {
        foreach (var row in new[] { Download, Verify, Install, FirstStart })
            if (row.State == RowState.Running) row.State = RowState.Pending;
        Embeddings.Halt();
    }

    double? Fraction(long received) => _bytes > 0 ? Math.Clamp((double)received / _bytes, 0, 1) : null;

    void StartInstall() {
        if (Install.State == RowState.Pending) Install.State = RowState.Running;
    }

    // The CLI marks no end of unpacking: the embedding step follows it. A reuse has nothing to install.
    void FinishInstall() {
        if (Install.State == RowState.Running) Install.State = RowState.Done;
    }

    void StartFirstStart() {
        Download.State = RowState.Done;
        Verify.State   = RowState.Done;
        Install.State  = RowState.Done;
        Embeddings.Close();
        if (FirstStart.State == RowState.Pending) FirstStart.State = RowState.Running;
    }

    void AllDone() {
        Download.State   = RowState.Done;
        Verify.State     = RowState.Done;
        Install.State    = RowState.Done;
        Embeddings.Close();
        FirstStart.State = RowState.Done;
    }
}
