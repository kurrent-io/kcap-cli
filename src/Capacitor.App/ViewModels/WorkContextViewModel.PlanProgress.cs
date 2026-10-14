using System.ComponentModel;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed partial class WorkContextViewModel {
    public bool ShowsPlanMergeNote => Plan.HasTasks && Plan.DoneCount < Plan.Tasks.Count
        && PullRequests is { SelectedIsMerged: true };

    public string PlanMergeHeading => ShowsPlanMergeNote ? $"PR {PullRequests!.NumberLabel} merged" : "";

    public string PlanMergeNote {
        get {
            if (!ShowsPlanMergeNote) return "";
            var remaining = Plan.Tasks.Count - Plan.DoneCount;
            var tasks = remaining == 1 ? "1 task is" : $"{remaining} tasks are";
            return $"{tasks} still marked open. This PR’s relationship to the plan is unconfirmed.";
        }
    }

    void OnPlanProgressChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName is nameof(PlanSectionViewModel.HeaderText)) RaisePlanProgress();
    }

    void RaisePlanProgress() {
        this.RaisePropertyChanged(nameof(ShowsPlanMergeNote));
        this.RaisePropertyChanged(nameof(PlanMergeHeading));
        this.RaisePropertyChanged(nameof(PlanMergeNote));
    }
}
