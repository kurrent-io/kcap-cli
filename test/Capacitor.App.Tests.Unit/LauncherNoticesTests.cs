using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.ViewModels;
using static Capacitor.App.ViewModels.LauncherNotices.Kind;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class LauncherNoticesTests {
    static readonly (bool, LauncherNotices.Kind) Off = (false, Problem);

    sealed class Rig : IDisposable {
        public readonly BehaviorSubject<(bool, LauncherNotices.Kind)> Stack = new(Off);
        public readonly BehaviorSubject<(bool, LauncherNotices.Kind)> Server = new(Off);
        public readonly BehaviorSubject<(bool, LauncherNotices.Kind)> Connection = new(Off);
        public readonly LauncherNotices Notices = new();

        public Rig() {
            Notices.TrackStack(Stack);
            Notices.TrackServer(Server);
            Notices.TrackConnection(Connection);
        }

        public (bool Stack, bool Server, bool Connection, int Hidden, string? Label) Shown =>
            (Notices.ShowStack, Notices.ShowServer, Notices.ShowConnection, Notices.HiddenCount, Notices.ToggleLabel);

        public void Dispose() => Notices.Dispose();
    }

    /// One card shows; the rest wait behind a count the toggle opens and closes.
    [Test]
    public async Task One_card_shows_until_the_rest_are_asked_for() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.Server.OnNext((true, Problem));
            rig.Connection.OnNext((true, Problem));

            await Assert.That(rig.Shown).IsEqualTo((false, true, false, 1, "1 more issue"));

            await rig.Notices.ToggleCommand.Execute().ToTask();
            await Assert.That(rig.Shown).IsEqualTo((false, true, true, 0, "Show fewer"));

            await rig.Notices.ToggleCommand.Execute().ToTask();
            await Assert.That(rig.Shown).IsEqualTo((false, true, false, 1, "1 more issue"));
        });
    }

    /// Something the user can act on outranks a card that is only reporting progress or a note, even
    /// from a later layer; among equals the earlier layer leads.
    [Test]
    [Arguments(Progress, Problem, false, true)]
    [Arguments(Note, Problem, false, true)]
    [Arguments(Problem, Problem, true, false)]
    [Arguments(Problem, Progress, true, false)]
    public async Task An_actionable_card_outranks_progress_and_notes(
            LauncherNotices.Kind stack, LauncherNotices.Kind connection, bool stackShown, bool connectionShown) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.Stack.OnNext((true, stack));
            rig.Connection.OnNext((true, connection));

            await Assert.That(rig.Shown).IsEqualTo((stackShown, false, connectionShown, 1, "1 more issue"));
        });
    }

    /// A local stack card that is not a note already covers the server, so the server card folds into
    /// it; behind a note, the server card is a problem of its own and outranks it.
    [Test]
    [Arguments(Problem, false, 0)]
    [Arguments(Progress, false, 0)]
    [Arguments(Note, true, 1)]
    public async Task A_stack_card_absorbs_the_local_server_card(LauncherNotices.Kind stack, bool serverShown, int hidden) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.Stack.OnNext((true, stack));
            rig.Server.OnNext((true, Problem));

            await Assert.That(rig.Notices.ShowServer).IsEqualTo(serverShown);
            await Assert.That(rig.Notices.ShowStack).IsEqualTo(!serverShown);
            await Assert.That(rig.Notices.HiddenCount).IsEqualTo(hidden);
        });
    }

    /// Clearing down to one card folds the stack again, so the next pile starts with one card.
    [Test]
    public async Task Clearing_down_to_one_card_folds_the_stack_again() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.Server.OnNext((true, Problem));
            rig.Connection.OnNext((true, Problem));
            await rig.Notices.ToggleCommand.Execute().ToTask();

            rig.Server.OnNext(Off);
            await Assert.That(rig.Shown).IsEqualTo((false, false, true, 0, (string?)null));
            await Assert.That(rig.Notices.Expanded).IsFalse();

            rig.Server.OnNext((true, Problem));
            await Assert.That(rig.Shown).IsEqualTo((false, true, false, 1, "1 more issue"));
        });
    }
}
