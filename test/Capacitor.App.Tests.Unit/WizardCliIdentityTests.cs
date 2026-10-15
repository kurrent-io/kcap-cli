using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Tests.Unit;

public class WizardCliIdentityTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed class Runner : IProcessRunner {
        public RunOptions? Options;
        public string[]? Args;
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Options = options;
            Args = args;
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }
        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
                Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    sealed class Probe : ILoginShellProbe {
        public Task<string?> TerminalPathAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<bool?> KcapOnPathAsync(CancellationToken ct, bool forceRefresh = false) => Task.FromResult<bool?>(null);
        public Task<string?> KcapPathAsync(CancellationToken ct, bool forceRefresh = false) => Task.FromResult<string?>(null);
    }

    [Test]
    public async Task Wizard_cli_uses_the_effective_profile_while_consent_ack_stays_literal() {
        var config = new ProfileConfig {
            ActiveProfile = "personal",
            Profiles = new() {
                ["personal"] = new Profile { ServerUrl = "https://personal.example", Daemon = new() { Name = "personal-daemon" } },
                ["work"] = new Profile { ServerUrl = "https://work.example", Daemon = new() { Name = "work-daemon" } }
            }
        };
        File.WriteAllText(AppConfig.GetConfigPath(Config.Root), JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig));
        var runner = new Runner();
        var env = new ProfileOverrides(null, "work");
        var cli = App.NewWizardCli(Config.Root, env, runner, "/test/kcap", new Probe());

        await cli.PluginInstallAsync("--cursor", CancellationToken.None);
        await Assert.That(runner.Options!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");
        await Assert.That(App.ResolveWizardIdentity(Config.Root, env)!.Value.Profile).IsEqualTo("work");
        await Assert.That(App.ResolveConsentFlipIdentity(Config.Root).Profile).IsEqualTo("personal");

        await cli.ServiceStatusAsync(CancellationToken.None);
        await Assert.That(runner.Args).Contains("work-daemon");
    }

    [Test]
    public async Task Setup_restart_discloses_import_interruption_only_when_an_import_is_running() {
        await Assert.That(App.SetupRestartPrompt(true).Disclosure).Contains("Your history import will stop");
        await Assert.That(App.SetupRestartPrompt(true).Disclosure).Contains("Sessions already imported are kept");
        await Assert.That(App.SetupRestartPrompt(false).Disclosure).DoesNotContain("history import");
    }
}
