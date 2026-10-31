using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// A folder whose sessions matched no repository, offered on the History page apart from the repositories.
/// Unchecked until the user checks it, so an untouched page imports the repositories alone, and Only me
/// once checked: with no repository there is no owner to tell who else should read it.
/// </summary>
public sealed class HistoryFolderRow : ReactiveObject {
    bool _included;
    bool _onlyMe = true;

    internal HistoryFolderRow(string path, int sessions, IReadOnlyList<ImportDiscoveryWindow>? windows, bool local) {
        Path     = path;
        Sessions = sessions;
        Windows  = windows ?? [];
        Local    = local;
    }

    public string Path     { get; }
    public int    Sessions { get; }
    public bool   Local    { get; }

    internal IReadOnlyList<ImportDiscoveryWindow> Windows { get; }

    public string SignalLine => $"{HistoryStepViewModel.SessionCount(Sessions)} on disk · no repository";

    public bool Included {
        get => _included;
        set {
            if (_included == value) return;
            this.RaiseAndSetIfChanged(ref _included, value);
            this.RaisePropertyChanged(nameof(Level));
        }
    }

    public bool OnlyMe {
        get => _onlyMe || Local;
        set {
            if (Local || _onlyMe == value) return;
            this.RaiseAndSetIfChanged(ref _onlyMe, value);
            this.RaisePropertyChanged(nameof(Level));
        }
    }

    public ImportLevel Level => !Included ? ImportLevel.Skip : OnlyMe ? ImportLevel.OnlyMe : ImportLevel.Shared;

    /// Sessions inside the window that starts on <paramref name="since"/>, or null when the report did not count it.
    internal int? SessionsSince(DateOnly? since) => Windows.FirstOrDefault(w => w.Since == since)?.Sessions;
}
