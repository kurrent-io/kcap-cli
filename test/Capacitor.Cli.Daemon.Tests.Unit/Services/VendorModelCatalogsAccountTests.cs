using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The Codex catalog per account: the machine's default stays in <c>VendorModels</c>, each registered
/// Codex account's catalog is probed under its own directory into <c>VendorModelsByAccount</c>, and the
/// map follows the accounts snapshot.
/// </summary>
public class VendorModelCatalogsAccountTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    static readonly VendorModelOption Default = new("gpt-default", "Default");
    static readonly VendorModelOption Old     = new("gpt-old", "Old");

    static Task<IReadOnlyList<VendorModelOption>?> ModelNamedAfter(string directory) =>
        Task.FromResult<IReadOnlyList<VendorModelOption>?>([new(Path.GetFileName(directory), Path.GetFileName(directory))]);

    static Dictionary<string, VendorModelOption[]> Map(params (string Id, VendorModelOption[] Models)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.Models, StringComparer.Ordinal);

    AccountsSnapshotDto Snapshot(params (string Id, string Directory)[] codexAccounts) {
        var claude        = Tmp.PathTo(".claude-work");
        var codexDefault  = Tmp.PathTo(".codex");
        return new("host-1", 1,
            [
                .. codexAccounts.Select(a => new AccountViewDto(a.Id, "codex", a.Directory, DirectoryKey.Of(a.Directory), a.Id, "recording")),
                new AccountViewDto("claude-work", "claude", claude, DirectoryKey.Of(claude), "work", "recording"),
            ],
            [new DefaultDirectoryDto("codex", codexDefault, DirectoryKey.Of(codexDefault))]);
    }

    static (AgentOrchestrator Orchestrator, CaptureServerConnection Server, DaemonConfig Config) Build(
            StubCatalogFactory codex, Dictionary<string, VendorModelOption[]>? byAccount) {
        var server = new CaptureServerConnection();
        DaemonConfig? captured = null;
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            extraRuntimeFactories: [codex],
            configure: config => {
                config.UnattendedVendors            = [];
                config.UnattendedVendorCapabilities = [];
                config.SupportedVendors             = [codex.Vendor];
                config.VendorModels                 = new(StringComparer.Ordinal) { ["codex"] = [Default] };
                config.VendorModelsByAccount        = byAccount;
                captured = config;
            });
        return (orch, server, captured!);
    }

    /// The unregistered default has no registry id, so its catalog is the machine's; a Claude account
    /// is never probed, since its catalog does not depend on the account.
    [Test]
    public async Task Default_catalog_stays_in_vendor_models_and_accounts_land_in_the_map() {
        var work   = Tmp.PathTo(".codex-work");
        var spare  = Tmp.PathTo(".codex-spare");
        var codex  = new StubCatalogFactory("codex", [Default]) { ProbeDirectory = ModelNamedAfter };
        var config = new DaemonConfig { SupportedVendors = ["codex"] };

        await DaemonRunner.ProbeVendorCatalogsAsync(
            config, [codex], Snapshot(("acct-work", work), ("acct-spare", spare)), CancellationToken.None);

        await Assert.That(config.VendorModels!.Keys).IsEquivalentTo(["codex"]);
        await Assert.That(config.VendorModels["codex"]).IsEquivalentTo([Default], CollectionOrdering.Matching);
        await Assert.That(config.VendorModelsByAccount!.Keys).IsEquivalentTo(["acct-work", "acct-spare"]);
        await Assert.That(config.VendorModelsByAccount["acct-work"].Single().Value).IsEqualTo(".codex-work");
        await Assert.That(config.VendorModelsByAccount["acct-spare"].Single().Value).IsEqualTo(".codex-spare");
        await Assert.That(codex.ProbeCalls).IsEqualTo(1);
        await Assert.That(codex.ProbedDirectories).IsEquivalentTo([work, spare]);
    }

    /// The work account's probe is still running when the account is removed; the answer that arrives
    /// afterwards names an account the snapshot no longer has, so neither it nor the old entry survives.
    [Test]
    public async Task Removing_an_account_drops_its_catalog_and_a_late_probe_result_is_discarded() {
        var work        = Tmp.PathTo(".codex-work");
        var spare       = Tmp.PathTo(".codex-spare");
        var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workAnswer  = new TaskCompletionSource<IReadOnlyList<VendorModelOption>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codex = new StubCatalogFactory("codex", [Default]) {
            ProbeDirectory = directory => {
                if (directory != work) return ModelNamedAfter(directory);

                workStarted.TrySetResult();
                return workAnswer.Task;
            },
        };
        var (orch, server, config) = Build(codex, Map(("acct-work", [Old]), ("acct-spare", [Old])));
        await using var _ = orch;
        var snapshot = Snapshot(("acct-work", work), ("acct-spare", spare));
        orch.AccountsSnapshot = () => snapshot;

        orch.RefreshAdvertisedCapabilities("accounts changed");
        await workStarted.Task.WaitAsync(Timeout);
        snapshot = Snapshot(("acct-spare", spare));
        workAnswer.SetResult([new("gpt-late", "Late")]);
        await orch.CapabilityRefreshForTest.WaitAsync(Timeout);

        await Assert.That(config.VendorModelsByAccount!.Keys).IsEquivalentTo(["acct-spare"]);
        await Assert.That(config.VendorModelsByAccount["acct-spare"].Single().Value).IsEqualTo(".codex-spare");
        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(1);
    }

    /// The machine catalog reprobes equal, so only the account map differs; the change still reaches
    /// the local status stream and the server, and both carry the new map.
    [Test]
    public async Task Map_only_change_republishes_locally_and_to_the_server() {
        var work  = Tmp.PathTo(".codex-work");
        var codex = new StubCatalogFactory("codex", [Default]) { ProbeDirectory = ModelNamedAfter };
        var (orch, server, config) = Build(codex, Map(("acct-work", [Old])));
        await using var _ = orch;
        orch.AccountsSnapshot = () => Snapshot(("acct-work", work));
        var vendorModels = config.VendorModels;
        var v0           = orch.StatusNotifierForTest.Version;

        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest.WaitAsync(Timeout);

        await Assert.That(VendorModelCatalogs.Equal(config.VendorModels, vendorModels)).IsTrue();
        await Assert.That(config.VendorModelsByAccount!["acct-work"].Single().Value).IsEqualTo(".codex-work");
        await Assert.That(orch.StatusNotifierForTest.Version).IsGreaterThan(v0);
        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(1);

        await using var connection = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);
        var connect = connection.BuildDaemonConnect("mac", [], [], null, config.Advertisement);
        await Assert.That(ReferenceEquals(connect.VendorModelsByAccount, config.VendorModelsByAccount)).IsTrue();

        var status = JsonSerializer.Deserialize(
            new DaemonStatusIpc(config, orch, server, orch.StatusNotifierForTest, TimeProvider.System).Snapshot(),
            StatusIpcJsonContext.Default.DaemonStatusDto)!;
        await Assert.That(status.Daemon.VendorModelsByAccount!["acct-work"].Single().Value).IsEqualTo(".codex-work");
    }
}
