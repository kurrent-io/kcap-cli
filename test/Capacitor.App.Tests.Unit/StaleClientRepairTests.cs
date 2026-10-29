using System.Runtime.Versioning;
using TUnit.Core.Enums;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Tests.Unit;

[ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
public class StaleClientRepairTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Runner(Func<string, string[], ProcessResult> answer) : IProcessRunner {
        public List<(string File, string[] Args)> Ran { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Ran.Add((fileName, args));
            return Task.FromResult(answer(fileName, args));
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine,
            CancellationToken ct) => throw new NotSupportedException();
    }

    static readonly ProcessResult Ok = new(0, "", "", false);

    /// Reports a stale copy at <paramref name="path"/> until it runs <paramref name="appCli"/> or <paramref name="fixedBy"/> holds.
    sealed class Copies(string path, string appCli, Func<bool>? fixedBy = null, ClientCopyKind kind = ClientCopyKind.OnPath) {
        public ILocalClientCopies For(string? searchPath) => new Scripted(() =>
            CanonicalPath.Resolve(path) == CanonicalPath.Resolve(appCli) || fixedBy?.Invoke() == true
                ? []
                : [new ClientCopy(kind, path, "1.3.0-beta.8")]);
    }

    sealed class Scripted(Func<IReadOnlyList<ClientCopy>> find) : ILocalClientCopies {
        public IReadOnlyList<ClientCopy> Find() => find();
    }

    string AppCli() => Tmp.CreateExecutable("kcap.app/Contents/MacOS/kcap", "#!/bin/sh\nexit 0\n");

    static StaleClientRepair Repair(Runner runner, string appCli, Copies copies, string terminalPath = "/usr/bin:/bin") =>
        new(runner, new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>(terminalPath) }, appCli, copies.For);

    [Test]
    public async Task Nothing_older_changes_nothing() {
        var app    = AppCli();
        var runner = new Runner((_, _) => Ok);

        var outcome = await Repair(runner, app, new Copies(app, app)).RunAsync(CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new ClientRepairOutcome.Clear());
        await Assert.That(runner.Ran).IsEmpty();
    }

    [Test]
    public async Task An_older_kcap_in_a_writable_folder_becomes_a_link_to_the_apps_kcap() {
        var app   = AppCli();
        var stale = Tmp.CreateExecutable("bin/kcap", "#!/bin/sh\necho kcap 1.3.0-beta.8\n");
        var runner = new Runner((_, _) => Ok);

        var outcome = await Repair(runner, app, new Copies(stale, app)).RunAsync(CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new ClientRepairOutcome.Repaired(stale));
        await Assert.That(new FileInfo(stale).LinkTarget).IsEqualTo(app);
        await Assert.That(Directory.GetFiles(Tmp.PathTo("bin"))).IsEquivalentTo([stale]);
        await Assert.That(runner.Ran).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(Prompted))]
    public async Task An_older_kcap_in_a_folder_it_cannot_write_is_replaced_behind_the_administrator_prompt(
            ProcessResult answer, Type expected) {
        var app    = AppCli();
        var stale  = Tmp.CreateExecutable("locked/kcap", "#!/bin/sh\nexit 0\n");
        var runner = new Runner((_, _) => answer);
        var copies = new Copies(stale, app, () => answer.ExitCode == 0 && runner.Ran.Count > 0);
        File.SetUnixFileMode(Tmp.PathTo("locked"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try {
            var outcome = await Repair(runner, app, copies).RunAsync(CancellationToken.None);

            await Assert.That(outcome.GetType()).IsEqualTo(expected);
            await Assert.That(outcome).IsNotEqualTo(new ClientRepairOutcome.Clear());
            await Assert.That(runner.Ran.Single().File).IsEqualTo("/usr/bin/osascript");
            await Assert.That(runner.Ran.Single().Args[^3..]).IsEquivalentTo(["--", app, stale]);
        } finally {
            File.SetUnixFileMode(Tmp.PathTo("locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static IEnumerable<Func<(ProcessResult, Type)>> Prompted() {
        yield return () => (Ok, typeof(ClientRepairOutcome.Repaired));
        yield return () => (new ProcessResult(1, "", "execution error: User canceled. (-128)", false), typeof(ClientRepairOutcome.Cancelled));
        yield return () => (new ProcessResult(1, "", "ln: Operation not permitted", false), typeof(ClientRepairOutcome.Failed));
    }

    [Test]
    public async Task An_older_npm_install_is_updated_through_npm() {
        var app     = AppCli();
        var package = Tmp.CreateExecutable("lib/node_modules/@kurrent/kcap/bin/kcap", "#!/bin/sh\nexit 0\n");
        Tmp.CreateDir("bin");
        File.CreateSymbolicLink(Tmp.PathTo("bin", "kcap"), package);
        var npm      = Tmp.CreateExecutable("bin/npm", "#!/bin/sh\nexit 0\n");
        var updated  = false;
        var runner   = new Runner((file, _) => {
            updated |= file == npm;
            return Ok;
        });

        var outcome = await Repair(runner, app, new Copies(Tmp.PathTo("bin", "kcap"), app, () => updated), Tmp.PathTo("bin"))
            .RunAsync(CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new ClientRepairOutcome.Repaired(Tmp.PathTo("bin", "kcap")));
        await Assert.That(runner.Ran.Single().Args).IsEquivalentTo(["install", "--global", "@kurrent/kcap@latest"]);
        await Assert.That(new FileInfo(Tmp.PathTo("bin", "kcap")).LinkTarget).IsEqualTo(package);
    }

    [Test]
    public async Task An_npm_install_npm_cannot_update_becomes_a_link_to_the_apps_kcap() {
        var app     = AppCli();
        var package = Tmp.CreateExecutable("lib/node_modules/@kurrent/kcap/bin/kcap", "#!/bin/sh\nexit 0\n");
        Tmp.CreateDir("bin");
        File.CreateSymbolicLink(Tmp.PathTo("bin", "kcap"), package);
        Tmp.CreateExecutable("bin/npm", "#!/bin/sh\nexit 0\n");
        var runner = new Runner((_, _) => new ProcessResult(243, "", "npm ERR! code EACCES", false));

        var outcome = await Repair(runner, app, new Copies(Tmp.PathTo("bin", "kcap"), app), Tmp.PathTo("bin"))
            .RunAsync(CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new ClientRepairOutcome.Repaired(Tmp.PathTo("bin", "kcap")));
        await Assert.That(new FileInfo(Tmp.PathTo("bin", "kcap")).LinkTarget).IsEqualTo(app);
    }

    [Test]
    public async Task An_older_desktop_app_is_left_for_the_user() {
        var app   = AppCli();
        var other = Tmp.CreateExecutable("Other/kcap.app/Contents/MacOS/kcap", "#!/bin/sh\nexit 0\n");
        var runner = new Runner((_, _) => Ok);

        var outcome = await Repair(runner, app, new Copies(other, app, kind: ClientCopyKind.DesktopApp)).RunAsync(CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new ClientRepairOutcome.OldApp(other));
        await Assert.That(new FileInfo(other).LinkTarget).IsNull();
        await Assert.That(runner.Ran).IsEmpty();
    }

    /// A copy still older once it runs the app's kcap means the replacement did not take; repeating it would loop.
    [Test]
    public async Task A_copy_still_older_after_it_was_replaced_fails_instead_of_looping() {
        var app    = AppCli();
        var stale  = Tmp.CreateExecutable("bin/kcap", "#!/bin/sh\nexit 0\n");
        var runner = new Runner((_, _) => Ok);

        var outcome = await new StaleClientRepair(runner, new FakeLoginShellProbe(), app,
            _ => new Scripted(() => [new ClientCopy(ClientCopyKind.OnPath, stale, null)])).RunAsync(CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<ClientRepairOutcome.Failed>();
        await Assert.That(((ClientRepairOutcome.Failed)outcome).Path).IsEqualTo(stale);
    }
}
