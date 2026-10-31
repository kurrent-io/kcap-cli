using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class ProfilesSettingsViewModel : ReactiveObject, IDisposable {
    /// The confirmation for removing a profile. Removing the one the local daemon runs on deletes the
    /// daemon's only sign-in, so the prompt says so; with no attached daemon reporting a profile, it
    /// says it may.
    internal static string RemovePrompt(string name, string? daemonProfile) {
        const string server = " Nothing on the server changes.";
        var start = $"Remove profile {name}? Its saved sign-in is deleted too.";
        return daemonProfile is null
            ? start + " If the local daemon runs on this profile, it stays signed out until you sign in to it again." + server
            : string.Equals(name, daemonProfile, StringComparison.Ordinal)
                ? start + " The local daemon runs on this profile, so it stays signed out until you sign in to it again or install it on another profile." + server
                : start + server;
    }

    readonly ConfigRoot _config;
    readonly TokenStore _tokens;
    readonly OnboardingGate _gate;
    readonly string _boundProfile;
    readonly Func<string, string, CancellationToken, Task> _openSignIn;
    readonly Func<string, CancellationToken, Task<bool>> _confirmRemove;
    readonly CancellationToken _lifetime;
    IReadOnlyList<ProfileRow> _rows = [];
    string? _message;
    bool _isBusy;
    int _refreshGeneration;

    /// <param name="appLane">This app's own server connection, on the profile it was bound to at startup.</param>
    /// <param name="daemon">The local daemon, whose snapshot names the profile it runs on and its own connection.</param>
    public ProfilesSettingsViewModel(
            ConfigRoot config, TokenStore tokens, OnboardingGate gate, string boundProfile,
            Func<string, string, CancellationToken, Task> openSignIn,
            Func<string, CancellationToken, Task<bool>> confirmRemove,
            CancellationToken appLifetime = default, IObservable<ServerLaneStatus>? appLane = null,
            IDaemonClientService? daemon = null) {
        _config = config;
        _tokens = tokens;
        _gate = gate;
        _boundProfile = boundProfile;
        _openSignIn = openSignIn;
        _confirmRemove = confirmRemove;
        _lifetime = appLifetime;

        var idle = this.WhenAnyValue(x => x.IsBusy, busy => !busy);
        SignInCommand = ReactiveCommand.CreateFromTask<ProfileRow>(SignInAsync, idle);
        RemoveCommand = ReactiveCommand.CreateFromTask<ProfileRow>(RemoveAsync, idle);

        appLane?.Select(s => (s.State, s.Failure)).DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(lane => { _lane = lane; Redecorate(); })
            .DisposeWith(_subscriptions);
        daemon?.Status.CombineLatest(daemon.Snapshots.Select(s => (DaemonInfoDto?)s.Daemon).StartWith((DaemonInfoDto?)null),
                (status, info) => status.State == AttachState.Connected && info is not null
                    ? (info.Profile, Word: DaemonStatusText.Connection(status, info))
                    : ((string?)null, Word: (string?)null))
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(attached => { _daemon = attached; Redecorate(); })
            .DisposeWith(_subscriptions);
    }

    readonly CompositeDisposable _subscriptions = new();
    (ServerLaneState State, ConnectionFailure? Failure)? _lane;
    (string? Profile, string? Word) _daemon;

    public IReadOnlyList<ProfileRow> Rows {
        get => _rows;
        private set {
            this.RaiseAndSetIfChanged(ref _rows, value);
            this.RaisePropertyChanged(nameof(ListedRows));
        }
    }

    void Redecorate() => Rows = [.. _rows.Select(Decorate)];

    ProfileRow Decorate(ProfileRow row) {
        var (connection, attention) = ConnectionFor(row.Name, row.IsBound, _lane, _daemon);
        return row with { Connection = connection, ConnectionNeedsAttention = attention };
    }

    /// The app's own connection on the profile it is bound to, and the daemon's on the profile it reports. A daemon
    /// that does not say which profile it runs on is not guessed at.
    internal static (string? Text, bool Attention) ConnectionFor(
            string profile, bool bound, (ServerLaneState State, ConnectionFailure? Failure)? lane, (string? Profile, string? Word) daemon) {
        var app = bound && lane is { } l ? l.State switch {
            ServerLaneState.Connected  => ("This app: connected", false),
            ServerLaneState.Connecting => ("This app: connecting", false),
            ServerLaneState.Retrying when l.Failure is { } failure => ($"This app: can't reach the server ({failure.Reason})", true),
            ServerLaneState.Retrying   => ("This app: reconnecting", false),
            ServerLaneState.SignedOut  => ("This app: sign-in rejected", true),
            _                          => ((string?)null, false),
        } : (null, false);
        var ours = daemon.Word is not null && string.Equals(daemon.Profile, profile, StringComparison.Ordinal)
            ? ($"Daemon: {daemon.Word}", daemon.Word is not ("connected" or "connecting"))
            : ((string?)null, false);
        var text = string.Join(" · ", new[] { app.Item1, ours.Item1 }.OfType<string>());
        return (text.Length > 0 ? text : null, app.Item2 || ours.Item2);
    }

    public void Dispose() => _subscriptions.Dispose();

    public IReadOnlyList<ProfileRow> ListedRows => [.. _rows.Where(r => !r.IsPlaceholder)];

    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public bool IsBusy {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public ReactiveCommand<ProfileRow, Unit> SignInCommand { get; }
    public ReactiveCommand<ProfileRow, Unit> RemoveCommand { get; }

    /// Rebuilds the rows from a fresh read and reports whether the config was readable; an
    /// unreadable one keeps the rows shown and says so.
    public async Task<bool> RefreshAsync() => await ReadAsync() is not null;

    // The rows as graded from a fresh read, or null when the config could not be read. Only the
    // newest read publishes: an older one still grading rows would otherwise overwrite it with
    // profiles that have since changed. The caller gets its own snapshot either way.
    async Task<IReadOnlyList<ProfileRow>?> ReadAsync() {
        var generation = ++_refreshGeneration;
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(_config), out var config)) {
            Message = "Could not read the profile configuration.";
            return null;
        }

        var rows = new List<ProfileRow>();
        foreach (var (name, profile) in config.Profiles.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            rows.Add(new ProfileRow(name, profile.ServerUrl, name == config.ActiveName, name == _boundProfile,
                await StatusAsync(name, profile)));
        }
        var decorated = rows.Select(Decorate).ToList();
        if (generation == _refreshGeneration) Rows = decorated;
        return decorated;
    }

    // Refresh-free on purpose: grading a row must never spend a single-use refresh token.
    async Task<ProfileCredentialStatus> StatusAsync(string name, Profile profile) {
        if (!OnboardingGate.ValidServerUrl(profile.ServerUrl)) return ProfileCredentialStatus.NoServer;
        if (profile.AuthProvider is { } stamp
                && string.Equals(stamp.Provider, AuthProvider.None, StringComparison.OrdinalIgnoreCase)
                && ServerIdentity.SameServer(stamp.ServerUrl, profile.ServerUrl))
            return ProfileCredentialStatus.NoSignInNeeded;

        try {
            return await _gate.EvaluateResolvedAsync(name, profile, _lifetime) switch {
                GateResult.Complete                                                    => ProfileCredentialStatus.SignedIn,
                GateResult.Incomplete { Reason: GateReason.NoToken }                   => ProfileCredentialStatus.SignedOut,
                GateResult.Incomplete { Reason: GateReason.TokenUnusableBinding }      => ProfileCredentialStatus.OtherServer,
                GateResult.Incomplete { Reason: GateReason.TokenUnusableExpired }      => ProfileCredentialStatus.Expired,
                _                                                                      => ProfileCredentialStatus.NoServer
            };
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not read the sign-in status of profile '{name}': {ex.Message}");
            return ProfileCredentialStatus.Unreadable;
        }
    }

    async Task SignInAsync(ProfileRow row) {
        IsBusy = true;
        Message = null;
        try {
            if (await CurrentAsync(row) is not { } current) return;
            if (!current.CanSignIn) {
                Message = "This profile has no server to sign in to.";
                return;
            }
            await _openSignIn(current.Name, current.ServerUrl!, _lifetime);
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Message = $"Could not open sign-in: {ex.Message}";
        } finally { IsBusy = false; }
    }

    async Task RemoveAsync(ProfileRow row) {
        IsBusy = true;
        Message = null;
        try {
            if (await CurrentAsync(row) is not { } current) return;
            if (!current.CanRemove) {
                Message = "This profile cannot be removed.";
                return;
            }
            if (!await _confirmRemove(current.Name, _lifetime)) return;

            var result = await ProfileRemoval.RemoveAsync(_config, _tokens, current.Name, _lifetime);
            Message = result.Outcome switch {
                ProfileRemovalOutcome.Removed              => $"Profile {current.Name} removed.",
                ProfileRemovalOutcome.RemovedTokenRetained => $"Profile {current.Name} removed, but its sign-in file could not be deleted ({result.Detail}).",
                ProfileRemovalOutcome.IsActive             => $"Profile {current.Name} is the active profile and cannot be removed.",
                ProfileRemovalOutcome.IsDefault            => "The default profile cannot be removed.",
                ProfileRemovalOutcome.NotFound             => $"Profile {current.Name} no longer exists.",
                _                                          => "The profile configuration could not be read. Nothing was removed."
            };
            await RefreshAsync();
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Message = $"Could not remove the profile: {ex.Message}";
        } finally { IsBusy = false; }
    }

    // Every action re-reads first and judges the row against its own read, never the shared rows a
    // newer read may be about to replace: an unreadable file, or a row that no longer matches it,
    // is refused, not acted on.
    async Task<ProfileRow?> CurrentAsync(ProfileRow row) {
        if (await ReadAsync() is not { } fresh) return null;
        var current = fresh.FirstOrDefault(r => r.Name == row.Name);
        var sameServer = current is not null
            && (string.Equals(current.ServerUrl, row.ServerUrl, StringComparison.Ordinal)
                || ServerIdentity.SameServer(current.ServerUrl, row.ServerUrl));
        if (current is not null && sameServer) return current;

        Message = "This profile changed. The list was refreshed.";
        return null;
    }
}
