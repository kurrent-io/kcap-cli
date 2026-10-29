using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class LauncherNoticesTests {
    sealed class Rig : IDisposable {
        public readonly BehaviorSubject<(bool, bool)> Stack = new((false, false));
        public readonly BehaviorSubject<bool> Server = new(false);
        public readonly BehaviorSubject<bool> Connection = new(false);
        public readonly LauncherNotices Notices = new();

        public Rig() {
            Notices.Track(Stack);
            Notices.TrackServer(Server);
            Notices.TrackConnection(Connection);
        }

        public (bool Stack, bool Server, bool Connection, int Hidden, string? Label) Shown =>
            (Notices.ShowStack, Notices.ShowServer, Notices.ShowConnection, Notices.HiddenCount, Notices.ToggleLabel);

        public void Dispose() => Notices.Dispose();
    }

    /// Only the most blocking problem shows; the rest wait behind a count.
    [Test]
    public async Task Only_the_top_problem_shows_until_the_rest_are_asked_for() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.Server.OnNext(true);
            rig.Connection.OnNext(true);

            await Assert.That(rig.Shown).IsEqualTo((false, true, false, 1, "1 more issue"));

            await rig.Notices.ToggleCommand.Execute().ToTask();
            await Assert.That(rig.Shown).IsEqualTo((false, true, true, 0, "Show fewer"));

            await rig.Notices.ToggleCommand.Execute().ToTask();
            await Assert.That(rig.Shown).IsEqualTo((false, true, false, 1, "1 more issue"));
        });
    }

    /// A local stack problem already offers the server's fix, so the server card folds into it;
    /// a stack note does not, and the server card stays a problem of its own.
    [Test]
    [Arguments(false, false, "1 more issue")]
    [Arguments(true, true, "2 more issues")]
    public async Task A_stack_problem_absorbs_the_local_server_card(bool note, bool serverCounted, string label) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.Stack.OnNext((true, note));
            rig.Server.OnNext(true);
            rig.Connection.OnNext(true);

            await Assert.That(rig.Shown).IsEqualTo((true, false, false, serverCounted ? 2 : 1, label));

            await rig.Notices.ToggleCommand.Execute().ToTask();
            await Assert.That(rig.Notices.ShowServer).IsEqualTo(serverCounted);
            await Assert.That(rig.Notices.ShowConnection).IsTrue();
        });
    }

    /// Fixing problems down to one folds the stack again, so the next pile starts with one card.
    [Test]
    public async Task Clearing_down_to_one_problem_folds_the_stack_again() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.Server.OnNext(true);
            rig.Connection.OnNext(true);
            await rig.Notices.ToggleCommand.Execute().ToTask();

            rig.Server.OnNext(false);
            await Assert.That(rig.Shown).IsEqualTo((false, false, true, 0, (string?)null));
            await Assert.That(rig.Notices.Expanded).IsFalse();

            rig.Server.OnNext(true);
            await Assert.That(rig.Shown).IsEqualTo((false, true, false, 1, "1 more issue"));
        });
    }
}
