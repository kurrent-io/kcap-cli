using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class FeedbackWindowSmokeTests {
    sealed class SentApi : IFeedbackApi {
        public Task<FeedbackResult> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default) =>
            Task.FromResult<FeedbackResult>(new FeedbackResult.Sent("a@b.c"));
    }

    [Test]
    public Task Window_reflects_the_category_gates_send_and_swaps_to_the_confirmation() => AvaloniaSession.RunOnUiAsync(async () => {
        var trailer = new BehaviorSubject<string>("Sent from Kurrent Capacitor Desktop 1.0.3 · daemon d 1.0.3");
        using var vm = new FeedbackViewModel(new SentApi(), FeedbackCategory.Feedback, trailer, "macOS 15.6", null);
        var window = new FeedbackWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var bug      = window.FindControl<RadioButton>("BugChip")!;
            var feedback = window.FindControl<RadioButton>("FeedbackChip")!;
            var message  = window.FindControl<TextBox>("MessageInput")!;
            var send     = window.FindControl<Button>("SendButton")!;
            var sent     = window.FindControl<StackPanel>("SentPanel")!;

            await Assert.That(feedback.IsChecked).IsTrue();
            await Assert.That(bug.IsChecked).IsFalse();
            await Assert.That(send.IsEffectivelyEnabled).IsFalse();
            await Assert.That(message.Classes.Contains("kcapField")).IsTrue();

            message.Text = "Nice.";
            Dispatcher.UIThread.RunJobs();
            await Assert.That(send.IsEffectivelyEnabled).IsTrue();

            send.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(sent.IsVisible).IsTrue();
            await Assert.That(window.FindControl<TextBlock>("SentText")!.Text).IsEqualTo("Sent. Replies go to a@b.c.");
        } finally {
            window.Close();
        }
    });
}
