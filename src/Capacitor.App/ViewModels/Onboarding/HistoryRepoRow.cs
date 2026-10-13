using System.Globalization;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// One repository on the History page and how far its history goes.
public sealed class HistoryRepoRow : ReactiveObject {
    ImportLevel _level = ImportLevel.Shared;

    internal HistoryRepoRow(ImportDiscoveryRepo repo) {
        Owner         = repo.Owner;
        Name          = repo.Name;
        Sessions      = repo.Sessions;
        LastSessionAt = repo.LastSessionAt;
        Windows       = repo.Windows ?? [];
    }

    public string Owner { get; }
    public string Name  { get; }
    public string Slug  => $"{Owner}/{Name}";

    public int             Sessions      { get; }
    public DateTimeOffset? LastSessionAt { get; }

    internal IReadOnlyList<ImportDiscoveryWindow> Windows { get; }

    public string SignalLine => LastSessionAt is { } last
        ? $"{HistoryStepViewModel.SessionCount(Sessions)} on disk · last {last.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}"
        : $"{HistoryStepViewModel.SessionCount(Sessions)} on disk";

    public ImportLevel Level {
        get => _level;
        set {
            if (EqualityComparer<ImportLevel>.Default.Equals(_level, value)) return;
            this.RaiseAndSetIfChanged(ref _level, value);
            // The row's track binds Stop, not Level. A bulk set from the owner writes Level.
            this.RaisePropertyChanged(nameof(Stop));
        }
    }

    /// The level as the track's stop index.
    public int Stop {
        get => (int)Level;
        set {
            if (value is < 0 or > 2) return;
            Level = (ImportLevel)value;
        }
    }

    /// This repository's sessions inside the window that starts on <paramref name="since"/>, or null
    /// when the report did not count it — never a guessed zero.
    internal int? SessionsSince(DateOnly? since) =>
        Windows.FirstOrDefault(w => w.Since == since)?.Sessions;
}
