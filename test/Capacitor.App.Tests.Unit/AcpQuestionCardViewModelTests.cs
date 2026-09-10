using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class AcpQuestionCardViewModelTests {
    static AcpInteractionOption Opt(string id, string label, int? min = null, int? max = null) =>
        new() { OptionId = id, Label = label, MinSelections = min, MaxSelections = max };

    static PendingPermissionRequest Question(bool multi, params AcpInteractionOption[] options) =>
        PendingPermissionRequest.FromServer(new ServerElicitationRequest("s1", "q1", "Pick", options, multi), DateTimeOffset.UtcNow);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Options_with_identical_labels_stay_distinct_and_a_single_pick_submits_its_id() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var permissions = new FakePermissionService();
            permissions.Queue(PermissionResolveKind.Applied);
            var card = new AcpQuestionCardViewModel(Question(false, Opt("a", "Same"), Opt("b", "Same")), permissions);
            await Assert.That(card.Options.Select(o => o.OptionId)).IsEquivalentTo(new[] { "a", "b" });
            card.Options[1].PickCommand.Execute().Subscribe();
            await WaitUntilAsync(() => permissions.AcpAnswered.Count == 1, what: "submitted");
            await Assert.That(permissions.AcpAnswered[0].Answer.SelectedOptionIds).IsEquivalentTo(new[] { "b" });
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Multi_select_gates_submit_on_the_bounds() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var permissions = new FakePermissionService();
            var card = new AcpQuestionCardViewModel(Question(true, Opt("a", "A", 2, 3), Opt("b", "B", 2, 3), Opt("c", "C", 2, 3), Opt("d", "D", 2, 3)), permissions);
            await Assert.That(card.IsAnswered).IsFalse();
            card.Options[0].IsSelected = true;
            await Assert.That(card.IsAnswered).IsFalse();
            card.Options[1].IsSelected = true;
            await Assert.That(card.IsAnswered).IsTrue();
            card.Options[2].IsSelected = true;
            card.Options[3].IsSelected = true;
            await Assert.That(card.IsAnswered).IsFalse(); // over the max
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Free_text_answers_when_there_are_no_options() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var permissions = new FakePermissionService();
            permissions.Queue(PermissionResolveKind.Applied);
            var card = new AcpQuestionCardViewModel(Question(false), permissions);
            await Assert.That(card.HasOptions).IsFalse();
            await Assert.That(card.IsAnswered).IsFalse();
            card.FreeText = "  something ";
            await Assert.That(card.IsAnswered).IsTrue();
            card.SubmitCommand.Execute().Subscribe();
            await WaitUntilAsync(() => permissions.AcpAnswered.Count == 1, what: "submitted");
            await Assert.That(permissions.AcpAnswered[0].Answer.FreeText).IsEqualTo("something");
            await Assert.That(permissions.AcpAnswered[0].Answer.SelectedOptionIds).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_transport_failure_keeps_the_card_with_the_lanes_error_copy() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var permissions = new FakePermissionService();
            permissions.Queue(PermissionResolveKind.TransportFailure, "not_signed_in");
            var card = new AcpQuestionCardViewModel(Question(false, Opt("a", "A")), permissions);
            card.Options[0].PickCommand.Execute().Subscribe();
            await WaitUntilAsync(() => card.ErrorText is not null, what: "error shown");
            await Assert.That(card.ErrorText).IsEqualTo("Sign in to answer");
            await Assert.That(card.IsBusy).IsFalse();
        });
    }
}
