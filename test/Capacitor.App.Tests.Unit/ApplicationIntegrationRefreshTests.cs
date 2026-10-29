using Capacitor.App.Platform.Mac;
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
    [Arguments("exit")]
    [Arguments("timeout")]
    [Arguments("exception")]
    public async Task Failed_repair_reports_a_warning_and_retries_on_the_next_launch(string failure) {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var target = Tmp.PathTo("Applications/kcap.app");
        var fail = true;
        var runner = new Runner((_, _) => {
            if (fail && failure == "exception") throw new IOException("write failed");
            return fail ? new ProcessResult(1, "", "write failed", failure == "timeout") : new ProcessResult(0, "", "", false);
        });
        var refresh = new ApplicationIntegrationRefresh(config, runner);
        refresh.Schedule(target);
        var warning = await refresh.RunPendingAsync(target, CancellationToken.None);
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!).Contains("retry");
        await Assert.That(File.Exists(config.Path(ApplicationIntegrationRefresh.FileName))).IsTrue();
        fail = false;
        await Assert.That(await refresh.RunPendingAsync(target, CancellationToken.None)).IsNull();
        await refresh.RunPendingAsync(target, CancellationToken.None);
        await Assert.That(runner.Calls).IsEqualTo(2);
        await Assert.That(File.Exists(config.Path(ApplicationIntegrationRefresh.FileName))).IsFalse();
    }

    [Test]
    public async Task Cancelled_repair_propagates_and_keeps_the_retry_marker() {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var target = Tmp.PathTo("Applications/kcap.app");
        using var cancellation = new CancellationTokenSource();
        var runner = new Runner((_, _) => {
            cancellation.Cancel();
            return new ProcessResult(0, "", "", false);
        });
        var refresh = new ApplicationIntegrationRefresh(config, runner);
        refresh.Schedule(target);

        await Assert.That(async () => await refresh.RunPendingAsync(target, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(File.Exists(config.Path(ApplicationIntegrationRefresh.FileName))).IsTrue();
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
    public async Task Migration_handoff_repairs_an_owned_Codex_MCP_entry_and_the_installed_copy_removes_the_user_Applications_copy() {
        var apps = Tmp.CreateDir("Applications");
        var userApps = Tmp.CreateDir("home/Applications");
        var old = Path.Combine(userApps, "kcap.app");
        static void Bundle(string root, string version) {
            Directory.CreateDirectory(Path.Combine(root, "Contents/MacOS"));
            File.WriteAllText(Path.Combine(root, "Contents/MacOS/kcap-desktop"), "app");
            File.WriteAllText(Path.Combine(root, "Contents/Info.plist"),
                $"<plist><dict><key>CFBundleIdentifier</key><string>io.kurrent.kcap</string><key>CFBundleVersion</key><string>{version}</string></dict></plist>");
        }
        Bundle(old, "1.1.0");
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
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var refresh = new ApplicationIntegrationRefresh(config, runner);
        var retirement = new ApplicationRetirement(config, TimeProvider.System, _ => []);
        var mover = new ApplicationsMover(runner, (from, to) => { Directory.Move(from, to); return true; }, apps,
            activate: (target, _) => { refresh.Schedule(target); return Task.FromResult(true); }, userApplicationsDir: userApps,
            retirement: retirement);
        var outcome = await mover.MoveAsync(old, CancellationToken.None);
        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(Directory.Exists(old)).IsTrue();
        await retirement.RunPendingAsync(outcome.InstalledPath, CancellationToken.None);
        await Assert.That(Directory.Exists(old)).IsFalse();
        await refresh.RunPendingAsync(outcome.InstalledPath, CancellationToken.None);
        var commands = CodexConfigToml.ReadMcpServerCommands(codex);
        await Assert.That(commands.Count).IsGreaterThan(0);
        await Assert.That(commands.All(command => command.Command == Path.Combine(apps, "kcap.app", "Contents", "MacOS", "kcap"))).IsTrue();
        await Assert.That(File.ReadAllText(codex)).DoesNotContain(userApps);
        await Assert.That(File.Exists(Tmp.PathTo("home/.codex/hooks.json"))).IsFalse();
    }
}
