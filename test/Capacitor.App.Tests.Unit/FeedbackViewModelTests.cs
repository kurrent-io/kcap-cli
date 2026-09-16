using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.App.Tests.Unit;

public class FeedbackViewModelTests {
    const string TrailerA = "Sent from Kurrent Capacitor Desktop 1.0.3 · daemon d cli 1.0.3";
    const string TrailerB = "Sent from Kurrent Capacitor Desktop 1.0.3 · daemon d 1.0.3";

    sealed class ScriptedFeedbackApi : IFeedbackApi {
        readonly Queue<Func<FeedbackSubmission, FeedbackResult>> _script = new();
        public List<FeedbackSubmission> Sent { get; } = [];
        public ScriptedFeedbackApi Then(FeedbackResult result) { _script.Enqueue(_ => result); return this; }
        public ScriptedFeedbackApi ThenThrow(Exception ex) { _script.Enqueue(_ => throw ex); return this; }
        public Task<FeedbackResult> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default) {
            Sent.Add(submission);
            return Task.FromResult(_script.Dequeue()(submission));
        }
    }

    static (FeedbackViewModel Vm, ScriptedFeedbackApi Api, BehaviorSubject<string> Trailer, List<bool> SignIns) New(
            FeedbackCategory category = FeedbackCategory.Bug) {
        var api     = new ScriptedFeedbackApi();
        var trailer = new BehaviorSubject<string>(TrailerA);
        var signIns = new List<bool>();
        var vm      = new FeedbackViewModel(api, category, trailer, "macOS 15.6", () => signIns.Add(true));
        return (vm, api, trailer, signIns);
    }

    [Test]
    public async Task Send_is_disabled_on_empty_and_whitespace_text_and_over_the_composed_cap() {
        var (vm, _, _, _) = New();

        await Assert.That(vm.CanSend).IsFalse();
        vm.Message = "   \n";
        await Assert.That(vm.CanSend).IsFalse();
        var largest = new string('x', 8000 - 2 - TrailerA.Length);
        vm.Message = largest;
        await Assert.That(vm.CanSend).IsTrue();
        vm.Message = largest + "x";
        await Assert.That(vm.CanSend).IsFalse();
    }

    [Test]
    public async Task An_edit_and_a_new_trailer_each_announce_both_derived_properties() {
        var (vm, _, trailer, _) = New();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.Message = "It broke.";

        await Assert.That(raised).Contains(nameof(FeedbackViewModel.CanSend));
        await Assert.That(raised).Contains(nameof(FeedbackViewModel.Hint));

        raised.Clear();
        trailer.OnNext(TrailerB);

        await Assert.That(raised).Contains(nameof(FeedbackViewModel.CanSend));
        await Assert.That(raised).Contains(nameof(FeedbackViewModel.Hint));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_send_command_becomes_executable_when_the_message_is_filled() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var (vm, _, _, _) = New();
            var seen = new List<bool>();
            using var subscription = vm.SendCommand.CanExecute.Subscribe(seen.Add);

            vm.Message = "It broke.";

            await Assert.That(seen[0]).IsFalse();
            await Assert.That(seen[^1]).IsTrue();
        });

    [Test]
    public async Task Hint_names_the_attachment_and_the_remaining_allowance() {
        var (vm, _, _, _) = New();
        vm.Message = "abc";

        await Assert.That(vm.Hint).IsEqualTo(
            $"Attached automatically: desktop 1.0.3 · daemon d cli 1.0.3 · macOS 15.6 · {8000 - 2 - TrailerA.Length - 3} characters left");
    }

    [Test]
    public async Task Hint_reads_a_trailer_FeedbackTrailer_actually_built() {
        var built = FeedbackTrailer.Build("1.0.3", "d", "1.0.2", null);
        using var vm = new FeedbackViewModel(
            new ScriptedFeedbackApi(), FeedbackCategory.Bug, new BehaviorSubject<string>(built), "macOS 15.6", null);
        vm.Message = "abc";

        await Assert.That(vm.Hint).IsEqualTo(
            $"Attached automatically: desktop 1.0.3 · daemon d 1.0.2 · macOS 15.6 · {8000 - 2 - built.Length - 3} characters left");
    }

    [Test]
    public async Task Send_composes_the_trailer_and_the_desktop_source() {
        var (vm, api, _, _) = New(FeedbackCategory.Feedback);
        api.Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = " Nice. ";

        await vm.SendCommand.Execute().ToTask();

        await Assert.That(api.Sent).Count().IsEqualTo(1);
        await Assert.That(api.Sent[0].Message).IsEqualTo("Nice.\n\n" + TrailerA);
        await Assert.That(api.Sent[0].Category).IsEqualTo(FeedbackCategory.Feedback);
        await Assert.That(api.Sent[0].Source).IsEqualTo(FeedbackSource.Desktop);
        await Assert.That(vm.IsSent).IsTrue();
        await Assert.That(vm.ReporterEmail).IsEqualTo("a@b.c");
    }

    [Test]
    public async Task Unchanged_retry_resends_the_same_snapshot_even_if_the_daemon_version_arrived() {
        var (vm, api, trailer, _) = New();
        api.Then(new FeedbackResult.TemporarilyUnavailable(TimeSpan.FromSeconds(3))).Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = "It broke.";

        await vm.SendCommand.Execute().ToTask();
        await Assert.That(vm.Outcome).IsEqualTo("Couldn't reach Kurrent support (temporary) — try again in 3s.");
        trailer.OnNext(TrailerB);
        await Assert.That(vm.Hint).Contains("daemon d cli 1.0.3");
        await vm.SendCommand.Execute().ToTask();

        await Assert.That(api.Sent).Count().IsEqualTo(2);
        await Assert.That(api.Sent[1]).IsEqualTo(api.Sent[0]);
    }

    [Test]
    public async Task An_edit_after_a_refusal_mints_a_new_id_and_a_fresh_trailer() {
        var (vm, api, trailer, _) = New();
        api.Then(new FeedbackResult.RateLimited()).Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = "It broke.";
        await vm.SendCommand.Execute().ToTask();
        trailer.OnNext(TrailerB);

        vm.Message = "It broke twice.";
        await vm.SendCommand.Execute().ToTask();

        await Assert.That(api.Sent[1].ClientRequestId).IsNotEqualTo(api.Sent[0].ClientRequestId);
        await Assert.That(api.Sent[1].Message).IsEqualTo("It broke twice.\n\n" + TrailerB);
    }

    [Test]
    public async Task A_category_switch_after_a_refusal_mints_a_new_id() {
        var (vm, api, _, _) = New();
        api.Then(new FeedbackResult.RateLimited()).Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = "It broke.";
        await vm.SendCommand.Execute().ToTask();

        vm.Reopen(FeedbackCategory.Feedback);
        await vm.SendCommand.Execute().ToTask();

        await Assert.That(api.Sent[1].Category).IsEqualTo(FeedbackCategory.Feedback);
        await Assert.That(api.Sent[1].ClientRequestId).IsNotEqualTo(api.Sent[0].ClientRequestId);
    }

    [Test]
    public async Task A_lost_success_response_followed_by_an_edit_sends_a_new_id() {
        var (vm, api, _, _) = New();
        api.ThenThrow(new HttpRequestException("socket closed")).Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = "It broke.";
        await vm.SendCommand.Execute().ToTask();
        await Assert.That(vm.Outcome).IsEqualTo("Couldn't send the report: socket closed");
        await Assert.That(vm.CanSend).IsTrue();

        vm.Message = "It broke, really.";
        await vm.SendCommand.Execute().ToTask();

        await Assert.That(api.Sent[1].ClientRequestId).IsNotEqualTo(api.Sent[0].ClientRequestId);
    }

    [Test]
    public async Task Sent_then_send_another_starts_a_blank_report_with_a_new_id() {
        var (vm, api, _, _) = New();
        api.Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = "It broke.";
        await vm.SendCommand.Execute().ToTask();
        var first = vm.CurrentId;

        await vm.SendAnotherCommand.Execute().ToTask();

        await Assert.That(vm.IsSent).IsFalse();
        await Assert.That(vm.Message).IsEqualTo("");
        await Assert.That(vm.CurrentId).IsNotEqualTo(first);
    }

    [Test]
    public async Task Reopen_on_a_sent_window_starts_a_blank_report_with_the_requested_category() {
        var (vm, api, _, _) = New();
        api.Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = "It broke.";
        await vm.SendCommand.Execute().ToTask();

        vm.Reopen(FeedbackCategory.Feedback);

        await Assert.That(vm.IsSent).IsFalse();
        await Assert.That(vm.Category).IsEqualTo(FeedbackCategory.Feedback);
        await Assert.That(vm.Message).IsEqualTo("");
    }

    [Test]
    public async Task Every_refusal_shows_its_sentence_and_leaves_the_form_editable() {
        foreach (var refusal in new FeedbackResult[] {
                     new FeedbackResult.NotConfigured(), new FeedbackResult.Unavailable(), new FeedbackResult.NoEmailOnFile(),
                     new FeedbackResult.RateLimited(), new FeedbackResult.Invalid("Bad.") }) {
            var (vm, api, _, _) = New();
            api.Then(refusal);
            vm.Message = "It broke.";
            await vm.SendCommand.Execute().ToTask();

            await Assert.That(vm.Outcome).IsEqualTo(FeedbackResultMessages.ForRefusal(refusal));
            await Assert.That(vm.CanSend).IsTrue();
            await Assert.That(vm.SignInOffered).IsFalse();
        }
    }

    [Test]
    public async Task A_final_401_offers_sign_in() {
        var (vm, api, _, signIns) = New();
        api.ThenThrow(new CapacitorApiException(401, "unauthorized"));
        vm.Message = "It broke.";

        await vm.SendCommand.Execute().ToTask();

        await Assert.That(vm.Outcome).IsEqualTo(HomeViewModel.SignInExpiredNotice);
        await Assert.That(vm.SignInOffered).IsTrue();
        await vm.SignInCommand.Execute().ToTask();
        await Assert.That(signIns).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Reopen_while_a_send_is_in_flight_does_not_touch_the_snapshot() {
        var api     = new BlockingFeedbackApi();
        var trailer = new BehaviorSubject<string>(TrailerA);
        var vm      = new FeedbackViewModel(api, FeedbackCategory.Bug, trailer, "macOS 15.6", null);
        vm.Message = "It broke.";
        var send = vm.SendCommand.Execute().ToTask();
        await api.Started.Task;

        vm.Reopen(FeedbackCategory.Feedback);
        await Assert.That(vm.Category).IsEqualTo(FeedbackCategory.Bug);
        await Assert.That(vm.IsBusy).IsTrue();

        api.Release(new FeedbackResult.Sent("a@b.c"));
        await send;
        await Assert.That(api.Sent[0].Category).IsEqualTo(FeedbackCategory.Bug);
    }

    [Test]
    public async Task A_category_binding_that_fires_mid_send_leaves_the_snapshot_alone() {
        var api     = new BlockingFeedbackApi();
        var trailer = new BehaviorSubject<string>(TrailerA);
        var vm      = new FeedbackViewModel(api, FeedbackCategory.Bug, trailer, "macOS 15.6", null);
        vm.Message = "It broke.";
        var send = vm.SendCommand.Execute().ToTask();
        await api.Started.Task;
        var bound = vm.CurrentId;

        vm.Category = FeedbackCategory.Feedback;

        await Assert.That(vm.Category).IsEqualTo(FeedbackCategory.Bug);
        await Assert.That(vm.CurrentId).IsEqualTo(bound);

        api.Release(new FeedbackResult.Sent("a@b.c"));
        await send;
        await Assert.That(api.Sent[0].Category).IsEqualTo(FeedbackCategory.Bug);
        await Assert.That(api.Sent[0].ClientRequestId).IsEqualTo(bound);
    }

    [Test]
    public async Task An_edit_that_fires_mid_send_leaves_the_snapshot_alone() {
        var api     = new BlockingFeedbackApi();
        var trailer = new BehaviorSubject<string>(TrailerA);
        var vm      = new FeedbackViewModel(api, FeedbackCategory.Bug, trailer, "macOS 15.6", null);
        vm.Message = "It broke.";
        var send = vm.SendCommand.Execute().ToTask();
        await api.Started.Task;
        var bound = vm.CurrentId;

        vm.Message = "It broke twice.";

        await Assert.That(vm.Message).IsEqualTo("It broke.");
        await Assert.That(vm.CurrentId).IsEqualTo(bound);

        api.Release(new FeedbackResult.Sent("a@b.c"));
        await send;
        await Assert.That(api.Sent).Count().IsEqualTo(1);
        await Assert.That(api.Sent[0].Message).IsEqualTo("It broke.\n\n" + TrailerA);
        await Assert.That(api.Sent[0].ClientRequestId).IsEqualTo(bound);
    }

    [Test]
    public async Task The_hint_stops_counting_down_at_zero_once_the_cap_is_passed() {
        var (vm, _, _, _) = New();
        vm.Message = new string('x', 8000 - 2 - TrailerA.Length + 12);

        await Assert.That(vm.Hint).EndsWith("· 0 characters left");
        await Assert.That(vm.CanSend).IsFalse();
    }

    [Test]
    public async Task Dispose_releases_the_trailer_subscription() {
        var (vm, _, trailer, _) = New();
        vm.Dispose();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        trailer.OnNext(TrailerB);

        await Assert.That(raised).IsEmpty();
        await Assert.That(vm.Hint).Contains("daemon d cli 1.0.3");
    }

    [Test]
    public async Task Disposing_twice_is_a_no_op() {
        var (vm, _, _, _) = New();
        vm.Dispose();

        await Assert.That(vm.Dispose).ThrowsNothing();
    }

    sealed class BlockingFeedbackApi : IFeedbackApi {
        readonly TaskCompletionSource<FeedbackResult> _gate = new();
        public TaskCompletionSource Started { get; } = new();
        public List<FeedbackSubmission> Sent { get; } = [];
        public void Release(FeedbackResult result) => _gate.SetResult(result);
        public async Task<FeedbackResult> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default) {
            Sent.Add(submission);
            Started.TrySetResult();
            return await _gate.Task;
        }
    }
}
