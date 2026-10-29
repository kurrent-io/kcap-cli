using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services.Update;

/// Owns the update schedule and its UX: silent periodic checks, background download, one prompt
/// when a package is ready, and the hand-off to the updater at the very end of shutdown. Once a
/// package is ready nothing else is checked or downloaded this run — a later download, even a
/// failed one, deletes every other cached package.
public sealed class UpdateCoordinator {
    internal static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(4);
    internal const string CheckLabel = "Check for Updates…";
    // Velopack's updater on Windows kills every process running from the install folder, and the
    // app's daemon runs from there; elsewhere a background daemon waits for a turn boundary.
    internal static string ReadyDisclosure(bool windows) => windows
        ? "The app will restart to finish installing the update.\n\nInstalling stops the daemon and interrupts running agents mid-turn. Eligible agents resume when auto-resume is enabled. Other sessions may need manual attention."
        : "The app will restart to finish installing the update.\n\nA background daemon waits for agents to finish their turn, then restarts after the configured wait even if a turn is still running. Eligible agents resume when auto-resume is enabled. Other sessions may need manual attention. A daemon started in a terminal needs a manual restart.";

    readonly IAppUpdater _updater;
    readonly ILifecycleSurface _surface;
    readonly TimeProvider _time;
    readonly Action _quit;
    readonly CancellationToken _lifetime;
    readonly TimeSpan _initialDelay;
    readonly TimeSpan _interval;
    readonly BehaviorSubject<UpdateMenuItem> _menu;
    readonly BehaviorSubject<UpdateCheckProgress?> _checkProgress = new(null);
    readonly Action<UpdateCandidate>? _handingOver;
    readonly LifecycleLog? _log;
    readonly Lock _lock = new();

    Task? _inflight;
    Task? _offer;
    bool _manualPending;
    bool _progressVisible;
    UpdateCheckProgress? _currentProgress;
    UpdateCandidate? _ready;
    UpdateCandidate? _pendingApply;

    /// The single-flight lane (`_inflight`) guarantees only one check ever sets this; `Withdraw` only
    /// clears it, by compare-and-swap. Readers run
    /// on the UI thread via `RunMenuActionAsync`, and `CheckAsync` holds `_lock` across
    /// `RunCheckAsync`'s synchronous prefix (which can reach `_menu.OnNext` and `ConfirmAsync`) — so
    /// this must never block on `_lock`, or a UI-thread caller blocked on the same dialog deadlocks.
    UpdateCandidate? Ready {
        get => Volatile.Read(ref _ready);
        set => Volatile.Write(ref _ready, value);
    }

    /// <param name="handingOver">Runs immediately before the updater takes over, so the next process can tell
    /// it finishes this update.</param>
    public UpdateCoordinator(
            IAppUpdater updater, ILifecycleSurface surface, TimeProvider time, Action quit, CancellationToken lifetime,
            TimeSpan? initialDelay = null, TimeSpan? interval = null, Action<UpdateCandidate>? handingOver = null,
            LifecycleLog? log = null) {
        _updater      = updater;
        _surface      = surface;
        _time         = time;
        _quit         = quit;
        _lifetime     = lifetime;
        _initialDelay = initialDelay ?? DefaultInitialDelay;
        _interval     = interval ?? DefaultInterval;
        _menu         = new BehaviorSubject<UpdateMenuItem>(new UpdateMenuItem(updater.IsAvailable, CheckLabel));
        _handingOver  = handingOver;
        _log          = log;
    }

    public IObservable<UpdateMenuItem> MenuItem => _menu.AsObservable();
    public IObservable<UpdateCheckProgress?> CheckProgress => _checkProgress.DistinctUntilChanged();

    /// The version a restart would install, once its package is downloaded.
    public string? ReadyVersion => Ready?.Version;

    /// Applies a package left from a previous run, after the install-location guard and before any
    /// graph exists. Skipped on an update relaunch: a failed apply relaunches the old version with
    /// the same package still cached, and applying it again automatically would loop before any UI
    /// appeared. True means the process is being replaced.
    public static bool TryApplyPendingAtStartup(IAppUpdater updater, bool updateRelaunch,
            Action<UpdateCandidate>? handingOver = null, LifecycleLog? log = null) {
        if (!updater.IsAvailable || updateRelaunch) return false;
        if (updater.PendingRestart is not { } pending || !IsEligible(updater, pending)) return false;

        // A failed apply must fall through to the normal UI, where the schedule re-offers the
        // update visibly, rather than bricking every launch behind a repeating startup failure.
        try {
            handingOver?.Invoke(pending);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: could not record the update handover: {ex.Message}");
        }
        try {
            log?.Write("update_apply_pending", ("from", updater.InstalledVersion), ("to", pending.Version));
            updater.ApplyNow(pending);
            return true;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: applying the pending update failed: {ex.Message}");
            log?.Write("update_apply_failed", ("stage", "pending_at_startup"), ("to", pending.Version), ("error", ex.Message));
            return false;
        }
    }

    internal static bool IsEligible(IAppUpdater updater, UpdateCandidate candidate) {
        var installed = updater.InstalledVersion;
        if (installed is null) return false;
        if (candidate.IsPrerelease && !updater.AllowsPrerelease) return false;

        return PrereleaseSemver.IsNewer(candidate.Version, installed);
    }

    public void Start() {
        if (!_updater.IsAvailable) return;
        _ = RunScheduleAsync();
    }

    /// The tray item's action: a check while idle, the restart once a package is ready.
    public Task RunMenuActionAsync() {
        if (Offerable is { } ready) {
            RequestRestart(ready);
            return Task.CompletedTask;
        }

        return CheckAsync(manual: true);
    }

    /// The app menu's item, whose label never changes: a manual check while idle, the ready prompt
    /// again once a package is downloaded.
    public async Task CheckForUpdatesAsync() {
        if (Offerable is not { } ready) {
            await CheckAsync(manual: true).ConfigureAwait(false);
            return;
        }
        try {
            await OfferOnceAsync(ready).ConfigureAwait(false);
        } catch (OperationCanceledException) { }
    }

    /// A silent check now, after a change to which builds this install is offered. A downloaded
    /// package the change made ineligible stops being offered.
    public Task RecheckAsync() {
        if (!_updater.IsAvailable) return Task.CompletedTask;
        if (Ready is { } ready && !IsEligible(_updater, ready)) Withdraw(ready);
        lock (_lock) {
            // A check already running read the feed under the old choice, so this one runs after it.
            if (_inflight is { IsCompleted: false } running) return CheckAfterAsync(running);
            return CheckAsync(manual: false);
        }
    }

    async Task CheckAfterAsync(Task running) {
        await running.ConfigureAwait(false);
        await CheckAsync(manual: false).ConfigureAwait(false);
    }

    UpdateCandidate? Offerable => Ready is { } ready && IsEligible(_updater, ready) ? ready : null;

    void Withdraw(UpdateCandidate ready) {
        if (Interlocked.CompareExchange(ref _ready, null, ready) != ready) return;
        _log?.Write("update_withdrawn", ("to", ready.Version));
        _menu.OnNext(new UpdateMenuItem(true, CheckLabel));
    }

    /// Called by the shutdown sequence immediately before the platform shutdown call, so the
    /// updater's bounded wait only ever covers process exit.
    public void ApplyPendingOnExit() {
        UpdateCandidate? pending;
        lock (_lock) {
            pending = _pendingApply;
            _pendingApply = null;
        }
        if (pending is null) return;
        // The beta choice can change between accepting the restart and the exit.
        if (!IsEligible(_updater, pending)) {
            _log?.Write("update_withdrawn", ("to", pending.Version), ("stage", "on_exit"));
            return;
        }
        try {
            _handingOver?.Invoke(pending);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not record the update handover: {ex.Message}");
        }
        _log?.Write("update_apply_on_exit", ("from", _updater.InstalledVersion), ("to", pending.Version));
        _updater.ApplyOnExit(pending);
    }

    async Task RunScheduleAsync() {
        try {
            await Task.Delay(_initialDelay, _time, _lifetime).ConfigureAwait(false);
            while (!_lifetime.IsCancellationRequested) {
                await CheckAsync(manual: false).ConfigureAwait(false);
                await Task.Delay(_interval, _time, _lifetime).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) { }
    }

    Task CheckAsync(bool manual) {
        lock (_lock) {
            if (_inflight is { IsCompleted: false } running) {
                if (manual) {
                    _manualPending = true;
                    _progressVisible = true;
                    _checkProgress.OnNext(_currentProgress);
                }
                return running;
            }

            _progressVisible = manual;
            _inflight = RunCheckAsync(manual);
            return _inflight;
        }
    }

    async Task RunCheckAsync(bool manual) {
        var stage = "check";
        try {
            if (Ready is { } ready) {
                if (IsEligible(_updater, ready)) {
                    if (manual) await ReportAsync($"kcap {ready.Version} is ready to install.").ConfigureAwait(false);
                    return;
                }
                // The beta choice changed as this package was published, past RecheckAsync's withdrawal.
                Withdraw(ready);
            }

            SetProgress(new UpdateCheckProgress("Checking for updates…"));
            var candidate = await _updater.CheckAsync(_lifetime).ConfigureAwait(false);
            if (candidate is null) {
                SetProgress(null);
                if (manual) _log?.Write("update_check", ("result", "up_to_date"), ("manual", "true"));
                if (ConsumeManual(manual)) await ReportAsync($"You're up to date ({_updater.InstalledVersion}).").ConfigureAwait(false);
                return;
            }

            _log?.Write("update_available", ("from", _updater.InstalledVersion), ("to", candidate.Version), ("manual", manual ? "true" : "false"));
            stage = "download";
            var downloading = new UpdateCheckProgress($"Downloading kcap {candidate.Version}…");
            SetProgress(downloading);
            await _updater.DownloadAsync(candidate, _lifetime, percent => {
                lock (_lock) {
                    if (_currentProgress is not null)
                        SetProgress(downloading with { Percent = Math.Clamp(percent, 0, 100) });
                }
            }).ConfigureAwait(false);
            SetProgress(null);
            _log?.Write("update_downloaded", ("to", candidate.Version));
            stage = "prompt";
            Interlocked.Exchange(ref _ready, candidate);
            // The beta choice can change while the package downloads.
            if (!IsEligible(_updater, candidate)) {
                Withdraw(candidate);
                ConsumeManual(manual);
                return;
            }
            _menu.OnNext(new UpdateMenuItem(true, $"Restart to update to {candidate.Version}"));

            await OfferOnceAsync(candidate).ConfigureAwait(false);
            ConsumeManual(manual);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            SetProgress(null);
            Console.Error.WriteLine($"kcap: update {stage} failed: {ex.Message}");
            _log?.Write("update_failed", ("stage", stage), ("manual", manual ? "true" : "false"), ("error", ex.Message));
            if (ConsumeManual(manual))
                await ReportAsync(stage == "download"
                    ? "The update could not be downloaded. Try again later."
                    : "Could not check for updates. Try again later.").ConfigureAwait(false);
        } finally {
            SetProgress(null);
            ConsumeManual(manual);
        }
    }

    void SetProgress(UpdateCheckProgress? progress) {
        lock (_lock) {
            _currentProgress = progress;
            _checkProgress.OnNext(_progressVisible ? progress : null);
        }
    }

    /// One ready prompt at a time: a caller arriving while it is open joins it rather than queueing
    /// another behind it.
    Task OfferOnceAsync(UpdateCandidate candidate) {
        lock (_lock) {
            if (_offer is { IsCompleted: false } open) return open;
            _offer = OfferAsync(candidate);
            return _offer;
        }
    }

    async Task OfferAsync(UpdateCandidate candidate) {
        var prompt = new LifecyclePrompt(
            LifecyclePrompt.KindUpdateReady, candidate.Version, _updater.InstalledVersion, false,
            ReadyDisclosure(OperatingSystem.IsWindows()));
        var accepted = await _surface.ConfirmAsync(prompt, _lifetime).ConfigureAwait(false);
        _log?.Write(accepted ? "update_accepted" : "update_deferred", ("to", candidate.Version));
        // The beta choice can change while the prompt is open.
        if (accepted && Offerable == candidate) RequestRestart(candidate);
    }

    bool ConsumeManual(bool manual) {
        lock (_lock) {
            var pending = _manualPending;
            _manualPending = false;
            return manual || pending;
        }
    }

    void RequestRestart(UpdateCandidate candidate) {
        lock (_lock) _pendingApply = candidate;
        _quit();
    }

    Task ReportAsync(string message) =>
        _surface.ConfirmAsync(new LifecyclePrompt(LifecyclePrompt.KindUpdateInfo, null, _updater.InstalledVersion, false, message), _lifetime);
}
