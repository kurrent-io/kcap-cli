using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.App.Tests.Unit;

public class ApplicationIntegrationRefreshTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Runner(Func<string, string[], ProcessResult> run) : IProcessRunner {
        public int Calls;
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls++;
            return Task.FromResult(run(fileName, args));
        }
        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Failed_or_timed_out_repair_is_retried_on_the_next_launch(bool timedOut) {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var target = Tmp.PathTo("Applications/kcap.app");
        var fail = true;
        var runner = new Runner((_, _) => fail ? new ProcessResult(1, "", "write failed", timedOut) : new ProcessResult(0, "", "", false));
        var refresh = new ApplicationIntegrationRefresh(config, runner);
        refresh.Schedule(target);
        await Assert.That(async () => await refresh.RunPendingAsync(target, CancellationToken.None)).Throws<IOException>();
        await Assert.That(File.Exists(config.Path(ApplicationIntegrationRefresh.FileName))).IsTrue();
        fail = false;
        await refresh.RunPendingAsync(target, CancellationToken.None);
        await refresh.RunPendingAsync(target, CancellationToken.None);
        await Assert.That(runner.Calls).IsEqualTo(2);
        await Assert.That(File.Exists(config.Path(ApplicationIntegrationRefresh.FileName))).IsFalse();
    }

    [Test]
    public async Task Another_bundle_does_not_consume_the_pending_repair() {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var runner = new Runner((_, _) => throw new InvalidOperationException("Wrong app must not refresh"));
        var refresh = new ApplicationIntegrationRefresh(config, runner);
        refresh.Schedule(Tmp.PathTo("Applications/kcap.app"));
        await refresh.RunPendingAsync(Tmp.PathTo("Downloads/kcap.app"), CancellationToken.None);
        await Assert.That(runner.Calls).IsEqualTo(0);
        await Assert.That(File.Exists(config.Path(ApplicationIntegrationRefresh.FileName))).IsTrue();
    }

    [Test]
    public async Task Migration_handoff_repairs_an_owned_Codex_MCP_entry_after_removing_the_legacy_bundle() {
        var apps = Tmp.CreateDir("Applications");
        var old = Path.Combine(apps, "Kurrent Capacitor.app");
        var download = Tmp.CreateDir("Downloads/kcap.app");
        static void Bundle(string root, string version) {
            Directory.CreateDirectory(Path.Combine(root, "Contents/MacOS"));
            File.WriteAllText(Path.Combine(root, "Contents/MacOS/kcap-desktop"), "app");
            File.WriteAllText(Path.Combine(root, "Contents/Info.plist"),
                $"<plist><dict><key>CFBundleIdentifier</key><string>io.kurrent.capacitor</string><key>CFBundleVersion</key><string>{version}</string></dict></plist>");
        }
        Bundle(old, "1.0.0");
        Bundle(download, "1.1.0");
        var codex = Tmp.GetResolvedPath("home", ".codex", "config.toml");
        CodexConfigToml.RegisterKcapMcpServers(codex, () => Path.Combine(old, "Contents/MacOS/kcap"));
        var runner = new Runner((file, args) => {
            if (file == "ditto") {
                Bundle(args[1], "1.1.0");
            } else {
                if (args is not ["refresh", "--app-migration"]) throw new InvalidOperationException("Unexpected command");
                CodexConfigToml.RegisterKcapMcpServers(codex, () => file);
            }
            return new ProcessResult(0, "", "", false);
        });
        var refresh = new ApplicationIntegrationRefresh(new ConfigRoot(Tmp.CreateDir("config")), runner);
        var mover = new ApplicationsMover(runner, (from, to) => { Directory.Move(from, to); return true; }, apps,
            activate: (target, _) => { refresh.Schedule(target); return Task.FromResult(true); });
        var outcome = await mover.MoveAsync(download, CancellationToken.None);
        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(Directory.Exists(old)).IsFalse();
        await refresh.RunPendingAsync(outcome.InstalledPath, CancellationToken.None);
        var commands = CodexConfigToml.ReadMcpServerCommands(codex);
        await Assert.That(commands.Count).IsGreaterThan(0);
        await Assert.That(commands.All(command => command.Command == Path.Combine(apps, "kcap.app", "Contents", "MacOS", "kcap"))).IsTrue();
        await Assert.That(File.ReadAllText(codex)).DoesNotContain("Kurrent Capacitor.app");
        await Assert.That(File.Exists(Tmp.PathTo("home/.codex/hooks.json"))).IsFalse();
    }
}
