using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class LocalSearchStartupTests {
    const string Version = "1.4.1";
    const string Model = "model";
    static readonly string OldSet = new('a', 64);
    static readonly string CurrentSet = new('b', 64);
    static readonly string Digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Model)));

    [TempDir] public required TempDir Tmp { get; init; }

    LocalRoot Root => field ??= new(Tmp.CreateDir("local"));

    void Install(string state) {
        LocalStateFiles.PublishRegistry(Root, new InstallRegistry(new LocalInstallation("installation", 5123, "http://localhost:5123", Version), []));
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Builtin(OldSet));
        Tmp.CreateFile(["local", "models", OldSet, "model.onnx"], Model);
        if (state != "missing manifest")
            Tmp.CreateFile(["local", "bin", Version, "embedding-model.json"], state == "invalid manifest" ? "{" :
                $$"""{"model_id":"test","contract":1,"model_set":"{{CurrentSet}}","files":[{"name":"model.onnx","sha256":"{{Digest}}","size":5}]}""");
        if (state != "missing model") WriteModel(state == "truncated" ? "m" : state == "corrupt" ? "wrong" : Model);
    }

    void WriteModel(string content) => Tmp.CreateFile(["local", "models", CurrentSet, "model.onnx"], content);

    LocalSearchViewModel CreateViewModel(FakeKcapCli cli, out LocalSearchController search) {
        search = new LocalSearchController(() => cli, action => action());
        var upgrade = new LocalUpgradeController(() => cli, action => action());
        return new LocalSearchViewModel(() => LocalEmbeddingFile.Read(Root),
            _ => Task.FromResult<LocalEmbeddingModelManifest?>(null), Observable.Return(new ServerLaneStatus(ServerLaneState.Connected)),
            search, upgrade, () => Task.CompletedTask, CancellationToken.None, ImmediateScheduler.Instance,
            builtinModelReady: () => LocalBuiltinOffer.InstalledModelReady(Root));
    }

    [Test]
    [Arguments("missing model")]
    [Arguments("missing manifest")]
    [Arguments("invalid manifest")]
    [Arguments("truncated")]
    [Arguments("corrupt")]
    [Arguments("ready")]
    public async Task A_fresh_app_uses_the_installed_bundles_model_files_to_restore_search_state(string state) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            Install(state);
            var cli = new FakeKcapCli();
            using var vm = CreateViewModel(cli, out _);
            var ready = state == "ready";

            await Assert.That(vm.Choice.UseBuiltin).IsTrue();
            await Assert.That(vm.StateText).IsEqualTo(ready ? "search by meaning" : "keyword search");
            await Assert.That(vm.CanApply).IsEqualTo(!ready);
            await Assert.That(vm.ApplyLabel).IsEqualTo(ready ? "Save" : "Try again");
            await Assert.That(cli.LocalUpgradeCallCount).IsEqualTo(0);
        });
    }

    [Test]
    public async Task A_repaired_model_stays_ready_after_another_app_restart() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            Install("missing model");
            var cli = new FakeKcapCli();
            using (var vm = CreateViewModel(cli, out var search)) {
                await Assert.That(vm.CanApply).IsTrue();
                cli.SetupLocalBehavior = (onLine, interrupt, ct) => {
                    WriteModel(Model);
                    LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Builtin(CurrentSet));
                    return LocalSetupScript.Prints(0,
                        """{"v":1,"event":"embeddings_configured","provider":"builtin"}""", LocalSetupScript.Committed)(onLine, interrupt, ct);
                };
                await vm.ApplyCommand.Execute().ToTask();
                await search.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(vm.CanApply).IsFalse();
            }

            using var restarted = CreateViewModel(new FakeKcapCli(), out _);
            await Assert.That(restarted.StateText).IsEqualTo("search by meaning");
            await Assert.That(restarted.CanApply).IsFalse();
            await Assert.That(restarted.ApplyLabel).IsEqualTo("Save");
        });
    }
}
