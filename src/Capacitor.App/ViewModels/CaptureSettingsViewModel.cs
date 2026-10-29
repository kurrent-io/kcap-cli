using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class CaptureSettingsViewModel : ReactiveObject, IDisposable {
    readonly SettingsProfileStore _store;
    readonly CancellationTokenSource _lifetime;
    readonly Func<CancellationToken, Task<VisibilityOptionsRead?>>? _visibilityOptions;
    readonly string _storedVisibility;
    string? _visibility;
    string _allowedRepos;
    string _excludedRepos;
    string _allowedPaths;
    string _excludedPaths;
    string? _message;
    bool _busy;
    bool _saved;
    bool _loading;
    bool _visibilityEditable = true;

    public CaptureSettingsViewModel(SettingsProfileStore store, CancellationToken appLifetime = default,
            bool isLocal = false, Func<CancellationToken, Task<VisibilityOptionsRead?>>? visibilityOptions = null) {
        _store = store;
        VisibilityVisible = !isLocal;
        _visibilityOptions = visibilityOptions;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);
        var profile = store.LoadProfile();
        _storedVisibility = NormalizeVisibility(profile.DefaultVisibility);
        _visibility = _storedVisibility;
        _allowedRepos = string.Join('\n', profile.AllowedRepos);
        _excludedRepos = string.Join('\n', profile.ExcludedRepos);
        _allowedPaths = string.Join('\n', profile.AllowedPaths);
        _excludedPaths = string.Join('\n', profile.ExcludedPaths);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, this.WhenAnyValue(x => x.Busy, x => x.Loading, (busy, loading) => !busy && !loading));
    }

    public string ProfileName => _store.ProfileName;
    public bool VisibilityVisible { get; }
    public IReadOnlyList<VisibilityOption> VisibilityOptions { get; private set; } = HarnessesStepViewModel.AllVisibilityOptions;
    public string? Visibility { get => _visibility; set => this.RaiseAndSetIfChanged(ref _visibility, value); }
    public string AllowedRepos { get => _allowedRepos; set => this.RaiseAndSetIfChanged(ref _allowedRepos, value); }
    public string ExcludedRepos { get => _excludedRepos; set => this.RaiseAndSetIfChanged(ref _excludedRepos, value); }
    public string AllowedPaths { get => _allowedPaths; set => this.RaiseAndSetIfChanged(ref _allowedPaths, value); }
    public string ExcludedPaths { get => _excludedPaths; set => this.RaiseAndSetIfChanged(ref _excludedPaths, value); }
    public string? Message {
        get => _message;
        private set {
            this.RaiseAndSetIfChanged(ref _message, value);
            Saved = false;
        }
    }
    /// The save went through; the message then adds what it did not change.
    public bool Saved { get => _saved; private set => this.RaiseAndSetIfChanged(ref _saved, value); }
    /// A save in progress: the one Capture operation closing Settings must wait for.
    public bool Busy { get => _busy; private set => this.RaiseAndSetIfChanged(ref _busy, value); }
    /// Reading the server's sharing options: read-only, so closing may abandon it.
    public bool Loading { get => _loading; private set => this.RaiseAndSetIfChanged(ref _loading, value); }
    /// False when the server's options could not be read; the stored choice is then kept as it is.
    public bool VisibilityEditable { get => _visibilityEditable; private set => this.RaiseAndSetIfChanged(ref _visibilityEditable, value); }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    public async Task RefreshVisibilityOptionsAsync() {
        if (!VisibilityVisible || _visibilityOptions is null || Busy || Loading) return;
        Loading = true;
        try {
            var read = await _visibilityOptions(_lifetime.Token);
            // Only a server that does not serve the route means "every stop"; a failed read says nothing
            // about this tenant, so it must not offer audiences the server may not honour.
            if (read is not { Problem: null } answered) {
                VisibilityEditable = false;
                return;
            }
            VisibilityEditable = true;
            var visibility = Visibility;
            VisibilityOptions = answered.Offer is { } offer
                ? [.. offer.Options.Select(o => new VisibilityOption(o.Key!, o.Label!, o.Detail ?? ""))]
                : HarnessesStepViewModel.AllVisibilityOptions;
            this.RaisePropertyChanged(nameof(VisibilityOptions));
            // Replacing the items clears the view's two-way selection before it can select the new item.
            Visibility = VisibilityOptions.Any(o => o.Value == visibility) ? visibility : null;
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            VisibilityEditable = false;
            Console.Error.WriteLine($"kcap: settings visibility options read failed: {ex.Message}");
        } finally { Loading = false; }
    }

    internal async Task SaveAsync() {
        if (Busy || Loading) return;
        var visibility = !VisibilityVisible ? "private" : VisibilityEditable ? Visibility : _storedVisibility;
        if (visibility is null || (VisibilityEditable && VisibilityVisible && !VisibilityOptions.Any(o => o.Value == visibility))) {
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
        if (allowedPaths.Concat(excludedPaths).Any(p => !Path.IsPathFullyQualified(p) && !IsHomeRelative(p))) {
            Message = "Enter absolute folder paths or paths starting with ~/, one per line.";
            return;
        }
        Busy = true;
        try {
            await _store.SaveCaptureAsync(new Profile {
                DefaultVisibility = visibility, AllowedRepos = allowedRepos, ExcludedRepos = excludedRepos,
                AllowedPaths = allowedPaths, ExcludedPaths = excludedPaths,
            }, _lifetime.Token, keepVisibility: VisibilityVisible && !VisibilityEditable);
            Message = "Sessions already on the server are unchanged.";
            Saved = true;
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Message = $"Could not save capture settings: {ex.Message}";
        } finally { Busy = false; }
    }

    // The forms the CLI's path normaliser expands: ~/ everywhere, and ~\ where that is the separator.
    static bool IsHomeRelative(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
                    || path.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    // The CLI reads a hand-edited or migrated value the same way, so a "Private" on disk is not a
    // choice the page fails to recognise.
    static string NormalizeVisibility(string? raw) =>
        raw?.ToLowerInvariant() is { } lower && AppConfig.ValidVisibilities.Contains(lower) ? lower : "org_public";

    static string[] Lines(string? text) => (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal).ToArray();

    public void Dispose() {
        _lifetime.Cancel();
        SaveCommand.Dispose();
        _lifetime.Dispose();
    }
}
