using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// The daemon page's name field, written to the active profile through ConfigMutator only when the
/// daemon is enabled. No claim maintenance — claims key on {profile, server} and resolve the daemon
/// name at application time, so a rename here needs no second-store write.
public sealed class MachineNameViewModel : ReactiveObject {
    readonly ConfigRoot     _config;
    readonly Func<string?>? _resolveProfileName;

    string  _daemonName;
    bool    _satisfied;
    string? _message;

    /// <param name="resolveProfileName">Re-invoked per persist rather than captured; null or unresolved falls back to <c>c.ActiveProfile</c>.</param>
    public MachineNameViewModel(
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

    /// Saved at least once.
    public bool Saved {
        get => _satisfied;
        private set => this.RaiseAndSetIfChanged(ref _satisfied, value);
    }

    public string DaemonName {
        get => _daemonName;
        set => this.RaiseAndSetIfChanged(ref _daemonName, value);
    }

    /// Set when a save fails, so the refusal is visible, not just logged.
    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    /// False, with <see cref="Message"/> set, when the profile could not be written.
    public async Task<bool> SaveAsync(CancellationToken ct) {
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
            Message = $"Could not save the machine name: {ex.Message}";

            return false;
        }

        Message = null;
        Saved   = true;

        return true;
    }
}
