using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class ProfilesSettingsViewModel : ReactiveObject {
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

    public ProfilesSettingsViewModel(
            ConfigRoot config, TokenStore tokens, OnboardingGate gate, string boundProfile,
            Func<string, string, CancellationToken, Task> openSignIn,
            Func<string, CancellationToken, Task<bool>> confirmRemove,
            CancellationToken appLifetime = default) {
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
    }

    public IReadOnlyList<ProfileRow> Rows {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

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

    /// Rebuilds the rows from a fresh read; an unreadable config keeps the rows shown and says so.
    public async Task RefreshAsync() {
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(_config), out var config)) {
            Message = "Could not read the profile configuration.";
            return;
        }

        var rows = new List<ProfileRow>();
        foreach (var (name, profile) in config.Profiles.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            rows.Add(new ProfileRow(name, profile.ServerUrl, name == config.ActiveName, name == _boundProfile,
                await StatusAsync(name, profile)));
        }
        Rows = rows;
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
                _                                          => "The profile configuration could not be read; nothing was removed."
            };
            await RefreshAsync();
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Message = $"Could not remove the profile: {ex.Message}";
        } finally { IsBusy = false; }
    }

    // Every action re-reads first: a row that no longer matches the file is refused, not acted on.
    async Task<ProfileRow?> CurrentAsync(ProfileRow row) {
        await RefreshAsync();
        var current = Rows.FirstOrDefault(r => r.Name == row.Name);
        var sameServer = current is not null
            && (string.Equals(current.ServerUrl, row.ServerUrl, StringComparison.Ordinal)
                || ServerIdentity.SameServer(current.ServerUrl, row.ServerUrl));
        if (current is not null && sameServer) return current;

        Message = "This profile changed; the list was refreshed.";
        return null;
    }
}
