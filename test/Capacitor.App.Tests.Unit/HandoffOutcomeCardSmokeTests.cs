using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Handoff;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

[NotInParallel(nameof(AvaloniaSession))]
public class HandoffOutcomeCardSmokeTests {
    [Test]
    public async Task A_long_outcome_scrolls_inside_the_card_and_leaves_the_transcript_its_height() {
        await RunOnUiAsync(async () => {
            using var session = new BehaviorSubject<ChatSessionInfo>(new ChatSessionInfo("Running", "Running", "gemini", null, null, null, false, "", null));
            using var outcomes = new Subject<HandoffOutcomeDto>();
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
                new ReadOnlyChatInput(), new NoAttachmentUploader(), null, new RecordingOpener(), time, new FakePermissionService(),
                new SessionRuns(time), handoffOutcome: outcomes);
            var view = new ChatTabView { DataContext = chat };
            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();
            try {
                outcomes.OnNext(new("s1", "Old", "done", null,
                    [.. Enumerable.Range(0, 40).Select(i => new HandoffOutcomeItemDto("plan", $"Plan {i}", "carried", null))]));
                await WorkspaceFixtures.WaitUntilAsync(() => {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    return view.FindControl<Border>("HandoffOutcomeCard")!.IsVisible;
                }, what: "the card");

                var card = view.FindControl<Border>("HandoffOutcomeCard")!;
                var transcript = view.FindControl<ItemsControl>("ChatItems")!;

                await Assert.That(card.Bounds.Height).IsLessThan(260);
                await Assert.That(transcript.Bounds.Height).IsGreaterThan(150);
            } finally {
                window.Close();
                await chat.TeardownAsync();
            }
        });
    }
}
