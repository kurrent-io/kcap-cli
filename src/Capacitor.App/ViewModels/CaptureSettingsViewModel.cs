using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Config;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class CaptureSettingsViewModel : ReactiveObject, IDisposable {
    readonly SettingsProfileStore _store;
    readonly CancellationTokenSource _lifetime;
    string? _visibility;
    string _allowedRepos;
    string _excludedRepos;
    string _allowedPaths;
    string _excludedPaths;
    string? _message;
    bool _busy;

    public CaptureSettingsViewModel(SettingsProfileStore store, CancellationToken appLifetime = default) {
        _store = store;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);
        var profile = store.LoadProfile();
        _visibility = profile.DefaultVisibility;
        _allowedRepos = string.Join('\n', profile.AllowedRepos);
        _excludedRepos = string.Join('\n', profile.ExcludedRepos);
        _allowedPaths = string.Join('\n', profile.AllowedPaths);
        _excludedPaths = string.Join('\n', profile.ExcludedPaths);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, this.WhenAnyValue(x => x.Busy).Select(b => !b));
    }

    public string ProfileName => _store.ProfileName;
    public IReadOnlyList<VisibilityOption> VisibilityOptions => HarnessesStepViewModel.VisibilityOptions;
    public string? Visibility { get => _visibility; set => this.RaiseAndSetIfChanged(ref _visibility, value); }
    public string AllowedRepos { get => _allowedRepos; set => this.RaiseAndSetIfChanged(ref _allowedRepos, value); }
    public string ExcludedRepos { get => _excludedRepos; set => this.RaiseAndSetIfChanged(ref _excludedRepos, value); }
    public string AllowedPaths { get => _allowedPaths; set => this.RaiseAndSetIfChanged(ref _allowedPaths, value); }
    public string ExcludedPaths { get => _excludedPaths; set => this.RaiseAndSetIfChanged(ref _excludedPaths, value); }
    public string? Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }
    public bool Busy { get => _busy; private set => this.RaiseAndSetIfChanged(ref _busy, value); }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    internal async Task SaveAsync() {
        if (Busy) return;
        if (Visibility is not { } visibility || !AppConfig.ValidVisibilities.Contains(visibility)) {
            Message = "Choose who can read new sessions.";
            return;
        }
        var allowedRepos = Lines(AllowedRepos);
        var excludedRepos = Lines(ExcludedRepos);
        if (allowedRepos.Concat(excludedRepos).Any(r => r.Split('/') is not [ { Length: > 0 }, { Length: > 0 } ] || r.Any(char.IsWhiteSpace))) {
            Message = "Enter repositories as owner/repo, one per line.";
            return;
        }
        var allowedPaths = Lines(AllowedPaths);
        var excludedPaths = Lines(ExcludedPaths);
        if (allowedPaths.Concat(excludedPaths).Any(p => !Path.IsPathFullyQualified(p) && p != "~" && !p.StartsWith("~/", StringComparison.Ordinal))) {
            Message = "Enter absolute folder paths or paths starting with ~/, one per line.";
            return;
        }
        Busy = true;
        try {
            await _store.SaveCaptureAsync(new Profile {
                DefaultVisibility = visibility, AllowedRepos = allowedRepos, ExcludedRepos = excludedRepos,
                AllowedPaths = allowedPaths, ExcludedPaths = excludedPaths,
            }, _lifetime.Token);
            Message = "Capture settings saved. Sessions already on the server are unchanged.";
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Message = $"Could not save capture settings: {ex.Message}";
        } finally { Busy = false; }
    }

    static string[] Lines(string? text) => (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal).ToArray();

    public void Dispose() {
        _lifetime.Cancel();
        SaveCommand.Dispose();
        _lifetime.Dispose();
    }
}
