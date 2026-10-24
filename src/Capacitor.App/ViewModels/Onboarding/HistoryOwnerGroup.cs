using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// Every repository under one owner, with checkboxes that set them all.
public sealed class HistoryOwnerGroup : ReactiveObject {
    internal HistoryOwnerGroup(string owner, IReadOnlyList<HistoryRepoRow> repos) {
        Owner = owner;
        Repos = repos;

        foreach (var repo in repos) {
            repo.PropertyChanged += (_, e) => {
                if (e.PropertyName != nameof(HistoryRepoRow.Level)) return;
                this.RaisePropertyChanged(nameof(Included));
                this.RaisePropertyChanged(nameof(OnlyMe));
                this.RaisePropertyChanged(nameof(OnlyMeEnabled));
                this.RaisePropertyChanged(nameof(Mixed));
                this.RaisePropertyChanged(nameof(SignalLine));
            };
        }
    }

    public string Owner { get; }
    public IReadOnlyList<HistoryRepoRow> Repos { get; }
    public bool Local => Repos[0].Local;

    /// The repositories disagree on whether or how they import.
    public bool Mixed => Repos.Select(r => r.Level).Distinct().Skip(1).Any();

    public string SignalLine =>
        (Repos.Count == 1 ? "1 repository" : $"{Repos.Count} repositories") + (Mixed ? " · mixed" : "");

    /// Null while some repositories import and some do not. Setting it sets every repository: a bulk
    /// set, not an inherited default.
    public bool? Included {
        get => Agreed(Repos, r => r.Included);
        set {
            if (value is not { } included) return;
            foreach (var repo in Repos) repo.Included = included;
        }
    }

    /// Read over the imported repositories only, and null while they disagree. A click on the mixed box
    /// arrives as false; it makes them all private, never all shared.
    public bool? OnlyMe {
        get => Agreed([.. Repos.Where(r => r.Included)], r => r.OnlyMe);
        set {
            if (value is not { } onlyMe) return;
            if (OnlyMe is null) onlyMe = true;
            foreach (var repo in Repos) repo.OnlyMe = onlyMe;
        }
    }

    public bool OnlyMeEnabled => Repos.Any(r => r.Included);

    static bool? Agreed(IReadOnlyList<HistoryRepoRow> repos, Func<HistoryRepoRow, bool> read) =>
        repos.Count == 0  ? false
        : repos.All(read) ? true
        : repos.Any(read) ? null
        :                   false;
}
