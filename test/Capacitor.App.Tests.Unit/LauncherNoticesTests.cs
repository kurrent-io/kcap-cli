using System.Reactive.Subjects;
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
        public readonly BehaviorSubject<bool> Update = new(false);
        public readonly LauncherNotices Notices = new();

        public Rig() {
            Notices.TrackStack(Stack);
            Notices.TrackServer(Server);
            Notices.TrackConnection(Connection);
            Notices.TrackUpdate(Update);
        }

        public (bool Stack, bool Server, bool Connection) Shown =>
            (Notices.ShowStack, Notices.ShowServer, Notices.ShowConnection);

        public void Dispose() => Notices.Dispose();
    }

    /// While an update is finishing its card is the only one, even over a problem: the others describe what the update is doing.
    [Test]
    public async Task A_finishing_update_stands_in_for_every_other_card() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.Server.OnNext((true, Problem));
            rig.Connection.OnNext((true, Progress));

            rig.Update.OnNext(true);
            await Assert.That(rig.Notices.ShowUpdate).IsTrue();
            await Assert.That(rig.Shown).IsEqualTo((false, false, false));

            rig.Update.OnNext(false);
            await Assert.That(rig.Notices.ShowUpdate).IsFalse();
            await Assert.That(rig.Shown).IsEqualTo((false, true, false));
        });
    }

    /// Only one card shows, and the next takes its place once it clears.
    [Test]
    public async Task One_card_shows_and_the_next_follows_when_it_clears() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            await Assert.That(rig.Shown).IsEqualTo((false, false, false));

            rig.Server.OnNext((true, Problem));
            rig.Connection.OnNext((true, Problem));
            await Assert.That(rig.Shown).IsEqualTo((false, true, false));

            rig.Server.OnNext(Off);
            await Assert.That(rig.Shown).IsEqualTo((false, false, true));
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

            await Assert.That(rig.Shown).IsEqualTo((stackShown, false, connectionShown));
        });
    }

    /// A local stack card that is not a note already covers the server, so the server card folds into it;
    /// behind a note, the server card is a problem of its own and outranks it.
    [Test]
    [Arguments(Problem, false)]
    [Arguments(Progress, false)]
    [Arguments(Note, true)]
    public async Task A_stack_card_absorbs_the_local_server_card(LauncherNotices.Kind stack, bool serverShown) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.Stack.OnNext((true, stack));
            rig.Server.OnNext((true, Problem));

            await Assert.That(rig.Shown).IsEqualTo((!serverShown, serverShown, false));
        });
    }
}
