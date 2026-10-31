using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using NSubstitute;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class HarnessesSettingsViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    SettingsProfileStore Seed() {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example" }
        } });
        return new SettingsProfileStore(Config.Root, "work", "https://work.example");
    }

    [Test]
    public Task Rescan_detects_new_harnesses_and_connect_handles_failure_then_retry() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        var wired = false;
        var detected = new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Codex] = new(true, false) };
        using var vm = new HarnessesSettingsViewModel(Seed(), cli, _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(detected), isWired: _ => wired);
        await vm.RescanAsync();
        await Assert.That(vm.Rows.Count).IsEqualTo(1);
        var row = vm.Rows[0];
        await Assert.That(row.Signal).IsEqualTo("Installed on this computer");
        await Assert.That(row.ConnectionLabel).IsNull();
        await Assert.That(row.ConsentNote).IsNotNull();
        cli.PluginInstallAsync("--codex", Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new ProcessResult(1, "", "installation failed", false), new ProcessResult(0, "", "", false));
        await vm.ConnectAsync(row);
        await Assert.That(row.Message!).DoesNotContain("installation failed");
        await Assert.That(row.Details).IsEqualTo("installation failed");
        wired = true;
        await vm.ConnectAsync(row);
        await Assert.That(row.Message!).Contains("Setup completed");
        await Assert.That(row.ConnectionLabel).IsEqualTo("Recording");
        detected[HarnessId.Cursor] = new(false, true);
        await vm.RescanAsync();
        await Assert.That(vm.Rows.Count).IsEqualTo(2);
        await Assert.That(vm.Rows.Single(r => r.Id == HarnessId.Cursor).Signal).Contains("settings folder");
    });

    [Test]
    public Task Setup_and_rescan_do_not_overlap_and_timeout_is_not_success() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        var completion = new TaskCompletionSource<ProcessResult>();
        cli.PluginInstallAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>()).Returns(completion.Task);
        var scans = 0;
        using var vm = new HarnessesSettingsViewModel(Seed(), cli, _ => {
            scans++;
            return Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(new Dictionary<HarnessId, DetectedAgent> {
                [HarnessId.Claude] = new(true, true)
            });
        });
        await vm.RescanAsync();
        var operation = vm.ConnectAsync(vm.Rows[0]);
        await Assert.That(vm.Busy).IsTrue();
        await vm.RescanAsync();
        await vm.ConnectAsync(vm.Rows[0]);
        await Assert.That(scans).IsEqualTo(1);
        completion.SetResult(new ProcessResult(0, "", "", true));
        await operation;
        await Assert.That(vm.Rows[0].Message!).Contains("timed out");
        await Assert.That(vm.Busy).IsFalse();
        await cli.Received(1).PluginInstallAsync(HarnessId.Claude.PluginInstallFlag, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>());
    });

    [Test]
    public Task Repointed_profile_refuses_setup_before_spawning_cli() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        using var vm = new HarnessesSettingsViewModel(Seed(), cli, _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(
            new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Codex] = new(true, true) }));
        await vm.RescanAsync();
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new(c.Profiles) {
            ["work"] = c.Profiles["work"] with { ServerUrl = "https://replacement.example" }
        } });
        await vm.ConnectAsync(vm.Rows[0]);
        await Assert.That(vm.Rows[0].Message!).Contains("profile changed");
        await cli.DidNotReceive().PluginInstallAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>());
    });
    [Test]
    public Task Unselecting_a_connected_harness_removes_setup_and_refreshes_its_state() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        var wired = true;
        cli.PluginRemoveAsync("--codex", Arg.Any<CancellationToken>()).Returns(_ => {
            wired = false;
            return new ProcessResult(0, "", "", false);
        });
        using var vm = new HarnessesSettingsViewModel(Seed(), cli, _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(
            new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Codex] = new(true, true) }), isWired: _ => wired);
        await vm.RescanAsync();
        var row = vm.Rows[0];
        await vm.ToggleConnectionAsync(row);
        await Assert.That(row.IsConfigured).IsFalse();
        await Assert.That(row.Message!).Contains("Disconnected");
        await vm.ToggleConnectionAsync(row);
        await cli.Received(1).PluginInstallAsync("--codex", Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>());
    });

    [Test]
    [Arguments(1, false, "removal failed")]
    [Arguments(0, true, "")]
    [Arguments(0, false, "")]
    public Task Removal_never_reports_success_while_still_wired_or_when_the_cli_fails(int exit, bool timedOut, string stderr) => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        cli.PluginRemoveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new ProcessResult(exit, "", stderr, timedOut));
        using var vm = new HarnessesSettingsViewModel(Seed(), cli, _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(
            new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Claude] = new(true, true) }), isWired: _ => true);
        await vm.RescanAsync();
        var row = vm.Rows[0];
        await vm.ToggleConnectionAsync(row);
        await Assert.That(row.IsConfigured).IsTrue();
        await Assert.That(row.Message!).DoesNotContain("Disconnected");
        await Assert.That(row.Message).IsNotNullOrEmpty();
    });

    [Test]
    public Task Disconnect_holds_the_busy_gate_and_refuses_a_repointed_profile() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        var completion = new TaskCompletionSource<ProcessResult>();
        cli.PluginRemoveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(completion.Task);
        using var vm = new HarnessesSettingsViewModel(Seed(), cli, _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(
            new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Codex] = new(true, true) }), isWired: _ => true);
        await vm.RescanAsync();
        var row = vm.Rows[0];
        var removal = vm.ToggleConnectionAsync(row);
        await Assert.That(vm.Busy).IsTrue();
        await vm.ToggleConnectionAsync(row);
        await vm.ConnectAsync(row);
        await vm.RescanAsync();
        completion.SetResult(new ProcessResult(0, "", "", false));
        await removal;
        await cli.Received(1).PluginRemoveAsync("--codex", Arg.Any<CancellationToken>());
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new(c.Profiles) {
            ["work"] = c.Profiles["work"] with { ServerUrl = "https://replacement.example" }
        } });
        await vm.ToggleConnectionAsync(row);
        await Assert.That(row.Message!).Contains("profile changed");
        await cli.Received(1).PluginRemoveAsync("--codex", Arg.Any<CancellationToken>());
    });

    [Test]
    public Task Disconnect_keeps_cleanup_warnings_visible_when_user_owned_tools_are_preserved() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        var wired = true;
        cli.PluginRemoveAsync("--codex", Arg.Any<CancellationToken>()).Returns(_ => {
            wired = false;
            return new ProcessResult(0, "", "Some MCP entries were preserved because they were edited. Review config.toml.", false);
        });
        using var vm = new HarnessesSettingsViewModel(Seed(), cli, _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(
            new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Codex] = new(true, true) }), isWired: _ => wired);
        await vm.RescanAsync();
        var row = vm.Rows[0];
        await vm.ToggleConnectionAsync(row);
        await Assert.That(row.IsConfigured).IsFalse();
        await Assert.That(row.ErrorTitle).IsEqualTo("Finished with warnings");
        await Assert.That(row.IsWarning).IsTrue();
        await Assert.That(row.Message).IsEqualTo(HarnessesSettingsViewModel.PartlyRemoved);
        await Assert.That(row.Details!).Contains("preserved");
    });


    /// Looking for tools is read-only, so it holds nothing Settings would wait on to close.
    [Test]
    public Task Rescanning_is_scanning_not_busy() => AvaloniaSession.RunOnUiAsync(async () => {
        var detect = new TaskCompletionSource<IReadOnlyDictionary<HarnessId, DetectedAgent>>();
        using var vm = new HarnessesSettingsViewModel(Seed(), Substitute.For<IKcapCli>(), _ => detect.Task);
        var scan = vm.RescanAsync();
        await Assert.That(vm.Scanning).IsTrue();
        await Assert.That(vm.Busy).IsFalse();
        detect.SetResult(new Dictionary<HarnessId, DetectedAgent>());
        await scan;
        await Assert.That(vm.Scanning).IsFalse();
    });

    /// Hooks call the bare kcap command, so a connect whose terminal command could not be put on
    /// the PATH installs nothing and says why.
    [Test]
    public Task Connect_stops_when_the_terminal_command_cannot_be_installed() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        using var vm = new HarnessesSettingsViewModel(Seed(), cli,
            _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Claude] = new(true, true) }),
            ensureTerminalCommand: _ => Task.FromResult<string?>("Cancelled. Nothing changed."));
        await vm.RescanAsync();
        await vm.ConnectAsync(vm.Rows[0]);
        await Assert.That(vm.Rows[0].ErrorTitle).IsNotNull();
        await Assert.That(vm.Rows[0].Message).IsEqualTo("Cancelled. Nothing changed.");
        await cli.DidNotReceive().PluginInstallAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>());
    });

    /// Install only ever adds, so turning the tools off for a recorded tool takes everything out
    /// and puts the hooks back alone; nothing on disk says tools are there, so the answer is kept.
    [Test]
    public Task Turning_tools_off_reinstalls_the_hooks_alone_and_remembers_it() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        cli.PluginRemoveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new ProcessResult(0, "", "", false));
        cli.PluginInstallAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new ProcessResult(0, "", "", false));
        var memory = new AppStateStore(Config.Root.Path("app-state.json"));
        using var vm = new HarnessesSettingsViewModel(Seed(), cli,
            _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Cursor] = new(false, true) }),
            isWired: _ => true, toolsMemory: memory);
        await vm.RescanAsync();
        var row = vm.Rows[0];
        await Assert.That(row.Tools).IsTrue();

        await vm.ToggleToolsAsync(row);

        Received.InOrder(() => {
            cli.PluginRemoveAsync(HarnessId.Cursor.PluginInstallFlag, Arg.Any<CancellationToken>());
            cli.PluginInstallAsync(HarnessId.Cursor.PluginInstallFlag, Arg.Any<CancellationToken>(),
                Arg.Is<IReadOnlyList<string>?>(o => o != null && o.SequenceEqual(new[] { "--skip-cursor-mcp", "--skip-cursor-skills" })));
        });
        await Assert.That(row.Tools).IsFalse();
        await Assert.That((await memory.LoadAsync()).HarnessToolsByVendor!["cursor"]).IsFalse();

        using var reopened = new HarnessesSettingsViewModel(Seed(), cli,
            _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Cursor] = new(false, true) }),
            isWired: _ => true, toolsMemory: memory);
        await reopened.RescanAsync();
        await Assert.That(reopened.Rows[0].Tools).IsFalse();
    });

    [Test]
    [Arguments(1, false)]
    [Arguments(0, true)]
    [Arguments(-1, false)]
    public Task Failed_reinstall_does_not_confirm_remembered_tools_after_rescan_or_reopen(int exitCode, bool timedOut) => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        var cli = Substitute.For<IKcapCli>();
        var wired = true;
        var retry = false;
        cli.PluginRemoveAsync("--cursor", Arg.Any<CancellationToken>()).Returns(_ => {
            wired = false;
            return new ProcessResult(0, "", "", false);
        });
        cli.PluginInstallAsync("--cursor", Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>()).Returns(_ => {
            if (!retry && exitCode == -1) throw new InvalidOperationException("reinstall could not start");
            if (!retry) return new ProcessResult(exitCode, "", "reinstall failed", timedOut);
            wired = true;
            return new ProcessResult(0, "", "", false);
        });
        var memory = new AppStateStore(Config.PathTo("app-state.json"));
        await memory.UpdateAsync(state => state with { HarnessToolsByVendor = new Dictionary<string, bool> { ["cursor"] = true } });
        static Task<IReadOnlyDictionary<HarnessId, DetectedAgent>> Detect(CancellationToken _) =>
            Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Cursor] = new(false, true) });
        using var vm = new HarnessesSettingsViewModel(store, cli, Detect, isWired: _ => wired, toolsMemory: memory);
        await vm.RescanAsync();
        var row = vm.Rows.Single();

        await vm.ToggleToolsAsync(row);

        await Assert.That(row.IsConfigured).IsFalse();
        await Assert.That(row.ErrorTitle).IsNotNull();
        await Assert.That(row.ToolsOnly).IsFalse();
        await Assert.That(row.ConnectionLabel).IsEqualTo("Setup unverified");
        await Assert.That((await memory.LoadAsync()).HarnessToolsByVendor!["cursor"]).IsTrue();
        await vm.RescanAsync();
        await Assert.That(vm.Rows.Single().ConnectionLabel).IsEqualTo("Setup unverified");
        using var reopened = new HarnessesSettingsViewModel(store, cli, Detect, isWired: _ => wired, toolsMemory: memory);
        await reopened.RescanAsync();
        await Assert.That(reopened.Rows.Single().ToolsOnly).IsFalse();
        await Assert.That(reopened.Rows.Single().ConnectionLabel).IsEqualTo("Setup unverified");

        retry = true;
        await reopened.ConnectAsync(reopened.Rows.Single());
        await reopened.RescanAsync();
        await Assert.That(reopened.Rows.Single().ConnectionLabel).IsEqualTo("Recording");
        await Assert.That(reopened.Rows.Single().ErrorTitle).IsNull();
    });

    [Test]
    public Task Tools_for_an_unrecorded_tool_install_tools_only() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        cli.PluginInstallAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new ProcessResult(0, "", "", false));
        using var vm = new HarnessesSettingsViewModel(Seed(), cli,
            _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Gemini] = new(true, true) }),
            isWired: _ => false);
        await vm.RescanAsync();
        await vm.ToggleToolsAsync(vm.Rows[0]);
        await cli.DidNotReceive().PluginRemoveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await cli.Received(1).PluginInstallAsync(HarnessId.Gemini.PluginInstallFlag, Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyList<string>?>(o => o != null && o.SequenceEqual(new[] { "--tools-only" })));
        await Assert.That(vm.Rows[0].ToolsOnly).IsTrue();
    });

    [Test]
    public Task A_removal_warning_survives_the_reinstall_that_follows_it() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        cli.PluginRemoveAsync("--cursor", Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, "", "Some MCP entries were preserved because they were edited.", false));
        cli.PluginInstallAsync("--cursor", Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new ProcessResult(0, "", "", false));
        using var vm = new HarnessesSettingsViewModel(Seed(), cli, _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(
            new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Cursor] = new(true, true) }), isWired: _ => true);
        await vm.RescanAsync();
        var row = vm.Rows[0];
        row.SetTools(true);

        await vm.ToggleToolsAsync(row);

        await Assert.That(row.Tools).IsFalse();
        await Assert.That(row.ErrorTitle).IsEqualTo("Finished with warnings");
        await Assert.That(row.Message).IsEqualTo(HarnessesSettingsViewModel.PartlyKept);
        await Assert.That(row.Details!).Contains("preserved");
    });
}
