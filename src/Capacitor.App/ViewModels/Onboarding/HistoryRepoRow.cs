using System.Globalization;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// One repository on the History page and how far its history goes.
public sealed class HistoryRepoRow : ReactiveObject {
    ImportLevel _level;
    ImportLevel _imported;

    /// <param name="local">The local edition has no other readers, so its one import level is private.</param>
    internal HistoryRepoRow(ImportDiscoveryRepo repo, bool local = false) {
        _level        = local ? ImportLevel.OnlyMe : ImportLevel.Shared;
        _imported     = _level;
        Local         = local;
        Owner         = repo.Owner;
        Name          = repo.Name;
        Sessions      = repo.Sessions;
        LastSessionAt = repo.LastSessionAt;
        Windows       = repo.Windows ?? [];
    }

    public string Owner { get; }
    public string Name  { get; }
    public string Slug  => $"{Owner}/{Name}";

    /// Imports are always private here, so Only me is not a choice.
    public bool Local { get; }

    public int             Sessions      { get; }
    public DateTimeOffset? LastSessionAt { get; }

    internal IReadOnlyList<ImportDiscoveryWindow> Windows { get; }

    public string SignalLine => LastSessionAt is { } last
        ? $"{HistoryStepViewModel.SessionCount(Sessions)} on disk · last {last.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}"
        : $"{HistoryStepViewModel.SessionCount(Sessions)} on disk";

    public ImportLevel Level {
        get => _level;
        set {
            if (Local && value == ImportLevel.Shared) value = ImportLevel.OnlyMe;
            if (_level == value) return;
            this.RaiseAndSetIfChanged(ref _level, value);
            if (value != ImportLevel.Skip) _imported = value;
            // The checkboxes bind these, and a bulk set from the owner writes Level.
            this.RaisePropertyChanged(nameof(Included));
            this.RaisePropertyChanged(nameof(OnlyMe));
        }
    }

    /// Carries the choice made on the row this one replaces, skipped or not, with the audience it would
    /// import at.
    internal void Adopt(HistoryRepoRow previous) {
        _imported = previous._imported;
        Level     = previous.Level;
    }

    /// Checking a repository again brings back the audience it had before it was skipped.
    public bool Included {
        get => Level != ImportLevel.Skip;
        set => Level = value ? _imported : ImportLevel.Skip;
    }

    /// Unchecked follows the recording privacy setting. A skipped repository keeps the audience for when
    /// it is checked again, so an owner-wide Only me reaches it too.
    public bool OnlyMe {
        get => Level == ImportLevel.OnlyMe;
        set {
            var audience = value || Local ? ImportLevel.OnlyMe : ImportLevel.Shared;
            if (Level == ImportLevel.Skip) _imported = audience;
            else Level = audience;
        }
    }

    /// This repository's sessions inside the window that starts on <paramref name="since"/>, or null
    /// when the report did not count it — never a guessed zero.
    internal int? SessionsSince(DateOnly? since) =>
        Windows.FirstOrDefault(w => w.Since == since)?.Sessions;
}
