using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Harness.MuseAssistant;
using static Capacitor.Cli.Tests.Unit.Harness.MuseAssistant.WalkBuilder;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>Argument shapes the cron and the skill rely on: a wrong shape is exit 2 with usage on stderr
/// and nothing written or posted, and no failure leaves the process outside exit codes 0 to 4.</summary>
public class MuseAssistantCommandTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome]       public required TempHome       Home   { get; init; }
    [TempDir]        public required TempDir        Walks  { get; init; }

    sealed class ThrowingStores(Exception failure) : MuseAssistantStateStoreFactory {
        public override MuseAssistantStateStore Create(string scopeDir, MuseAssistantPrincipal principal) => throw failure;
    }

    async Task<(MuseAssistantCommand Command, RecordingPoster Poster)> CommandAsync(MuseAssistantStateStoreFactory? stores = null) {
        var poster   = new RecordingPoster();
        var identity = await MuseAssistantTestIdentity.SignedInAsync(Config.Root);
        var runner   = new MuseAssistantActionRunner(poster, TimeProvider.System);
        stores     ??= new MuseAssistantStateStoreFactory();
        var ingest   = new MuseAssistantIngest(Config.Root, Home.Home, identity, stores, runner, TimeProvider.System);
        var scope    = new MuseAssistantScope(Config.Root, identity, stores, runner, TimeProvider.System);

        return (new MuseAssistantCommand(ingest, scope), poster);
    }

    [Test]
    [NotInParallel]
    [Arguments((object)new[] { "muse-assistant" })]
    [Arguments((object)new[] { "muse-assistant", "ingest" })]
    [Arguments((object)new[] { "muse-assistant", "ingest", "a", "b" })]
    [Arguments((object)new[] { "muse-assistant", "scope" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "add" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "add", "--chat", "c" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "add", "--chat", "--empty-chat" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "add", "--chat", "", "--empty-chat" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "add", "--chat", "c", "--empty-chat", "junk", "--bogus" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "add", "--chat", "c", "--empty-chat", "--include-history" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "add", "--chat", "c", "--after-seq", "5", "junk" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "add", "--empty-chat", "--chat", "c" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "remove", "--chat", "--empty-chat" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "remove", "--chat", "" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "remove", "--chat", "c", "junk" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "add", "--chat", "c", "--after-seq", "x" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "remove" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "reset", "--chat", "c" })]
    [Arguments((object)new[] { "muse-assistant", "scope", "dance" })]
    [Arguments((object)new[] { "muse-assistant", "sweep", "now" })]
    [Arguments((object)new[] { "muse-assistant", "dance" })]
    public async Task A_wrong_shape_is_exit_2_with_usage_and_writes_nothing(string[] args) {
        using var stderr = ConsoleOutput.StartErrorCapture();
        var (command, poster) = await CommandAsync();

        await Assert.That(await command.HandleAsync(args)).IsEqualTo(2);

        await Assert.That(stderr.GetCapturedError()).Contains("kcap muse-assistant");
        await Assert.That(poster.Accepted).IsEmpty();
        await Assert.That(Directory.Exists(Path.Join(Config.Directory, MuseAssistantPaths.Root))).IsFalse();
    }

    [Test]
    [NotInParallel]
    public async Task Scope_add_show_sweep_remove_and_reset_dispatch_to_the_scope_commands() {
        using var stdout = ConsoleOutput.StartCapture();
        var (command, _) = await CommandAsync();

        await Assert.That(await command.HandleAsync(["muse-assistant", "scope", "add", "--chat", "chat-main", "--empty-chat"])).IsEqualTo(0);
        await Assert.That(await command.HandleAsync(["muse-assistant", "scope", "show"])).IsEqualTo(0);
        await Assert.That(await command.HandleAsync(["muse-assistant", "sweep"])).IsEqualTo(0);
        await Assert.That(await command.HandleAsync(["muse-assistant", "scope", "remove", "--chat", "chat-main"])).IsEqualTo(0);
        await Assert.That(await command.HandleAsync(["muse-assistant", "scope", "reset"])).IsEqualTo(0);

        await Assert.That(stdout.GetCapturedOutput()).Contains("granted").And.Contains("removed chat=chat-main");
    }

    [Test]
    public async Task Ingest_of_a_missing_directory_is_exit_2() {
        var (command, _) = await CommandAsync();

        await Assert.That(await command.HandleAsync(["muse-assistant", "ingest", Config.PathTo("nope")])).IsEqualTo(2);
    }

    [Test]
    [NotInParallel]
    [Arguments("ingest", 1)]
    [Arguments("sweep", 1)]
    [Arguments("show", 1)]
    [Arguments("remove", 4)]
    [Arguments("reset", 4)]
    public async Task An_unexpected_exception_is_one_stderr_line_and_a_documented_exit_code(string verb, int exit) {
        using var stderr = ConsoleOutput.StartErrorCapture();
        var walk = new WalkBuilder(Walks).AddPage(null, null, false, User(1, 1_760_000_000_000)).Write();
        var (command, poster) = await CommandAsync(new ThrowingStores(new IOException("disk is full")));

        string[] args = verb switch {
            "ingest" => ["muse-assistant", "ingest", walk],
            "sweep"  => ["muse-assistant", "sweep"],
            "show"   => ["muse-assistant", "scope", "show"],
            "remove" => ["muse-assistant", "scope", "remove", "--chat", "chat-main"],
            _        => ["muse-assistant", "scope", "reset"]
        };

        await Assert.That(await command.HandleAsync(args)).IsEqualTo(exit);

        var lines = stderr.GetCapturedError().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("kcap muse-assistant").And.Contains("disk is full");
        await Assert.That(poster.Accepted).IsEmpty();
    }
}
