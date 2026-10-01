using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// The daemon-name field, written to the active profile through ConfigMutator on Next only. No claim maintenance — claims key on {profile, server} and resolve the daemon name
/// at application time, so a rename here needs no second-store write.
public sealed class DefaultsStepViewModel : ReactiveObject, IWizardStep {
    readonly ConfigRoot     _config;
    readonly Func<string?>? _resolveProfileName;

    string  _daemonName;
    bool    _satisfied;
    string? _message;

    /// <param name="resolveProfileName">Re-invoked per persist rather than captured; null or unresolved falls back to <c>c.ActiveProfile</c>.</param>
    public DefaultsStepViewModel(
            ConfigRoot     config,
            string?        defaultDaemonName  = null,
            Func<string?>? resolveProfileName = null
        ) {
        _config = config;

        _daemonName = string.IsNullOrWhiteSpace(defaultDaemonName)
            ? Environment.UserName.ToLowerInvariant()
            : defaultDaemonName;
        _resolveProfileName = resolveProfileName;
    }

    public WizardStepId Id         => WizardStepId.Defaults;
    public string       Title      => "This machine";
    public bool         Applicable => true;
    public string Lede => "What this machine is called when you start agents on it from elsewhere.";

    public bool Satisfied {
        get => _satisfied;
        private set => this.RaiseAndSetIfChanged(ref _satisfied, value);
    }

    public string DaemonName {
        get => _daemonName;
        set => this.RaiseAndSetIfChanged(ref _daemonName, value);
    }

    /// Set when a persist attempt fails, so the veto below is visible, not just logged.
    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public Task OnEnterAsync(CancellationToken ct) => Task.CompletedTask;

    /// Persists on Next only — Back and Skip leave the active profile untouched, so re-entering
    /// this step (or abandoning the wizard) never writes a value the user didn't confirm. A
    /// persist failure vetoes (stays on the step) with a visible Message rather than the shell's
    /// generic stderr-only catch.
    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        if (direction != WizardNavigation.Next) return true;

        try {
            await ConfigMutator.MutateAsync(_config, c => {
                var resolvedName = _resolveProfileName?.Invoke();
                var activeName   = resolvedName is not null && c.Profiles.ContainsKey(resolvedName)
                    ? resolvedName
                    : string.IsNullOrWhiteSpace(c.ActiveProfile) ? "default" : c.ActiveProfile;
                var profile    = c.Profiles.GetValueOrDefault(activeName) ?? new Profile();

                profile = profile with {
                    Daemon = (profile.Daemon ?? new DaemonSettings()) with { Name = DaemonName }
                };

                return c with { Profiles = new Dictionary<string, Profile>(c.Profiles) { [activeName] = profile } };
            }, ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Message = $"Could not save defaults: {ex.Message}";

            return false;
        }

        Message   = null;
        Satisfied = true;

        return true;
    }
}
