using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// Every repository under one owner, with one track that sets them all.
public sealed class HistoryOwnerGroup : ReactiveObject {
    internal HistoryOwnerGroup(string owner, IReadOnlyList<HistoryRepoRow> repos) {
        Owner = owner;
        Repos = repos;

        foreach (var repo in repos) {
            repo.PropertyChanged += (_, e) => {
                if (e.PropertyName != nameof(HistoryRepoRow.Level)) return;
                this.RaisePropertyChanged(nameof(Stop));
                this.RaisePropertyChanged(nameof(Mixed));
                this.RaisePropertyChanged(nameof(SignalLine));
            };
        }
    }

    public string Owner { get; }
    public IReadOnlyList<HistoryRepoRow> Repos { get; }

    /// The repositories disagree, so the owner track shows no level of its own.
    public bool Mixed => Repos.Select(r => r.Level).Distinct().Skip(1).Any();

    public string SignalLine =>
        (Repos.Count == 1 ? "1 repository" : $"{Repos.Count} repositories") + (Mixed ? " · mixed" : "");

    /// The shared stop, or -1 while mixed. Setting it sets every repository: a bulk set, not an
    /// inherited default.
    public int Stop {
        get => Mixed ? -1 : (int)Repos[0].Level;
        set {
            if (value is < 0 or > 2) return;
            foreach (var repo in Repos) repo.Level = (ImportLevel)value;
        }
    }
}
