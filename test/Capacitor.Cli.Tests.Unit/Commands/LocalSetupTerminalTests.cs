using System.Runtime.InteropServices;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

[ExcludeOn(OS.Windows)]
public class LocalSetupTerminalTests {
    const string Active    = "1.2.3";
    const string Requested = "2.0.0";

    [TempDir] public required TempDir Tmp { get; init; }

    public enum Given { Default, Builtin, Ollama, Skip }

    public enum Recorded { Nothing, Builtin, Ollama, Declined }

    /// <summary>Which answers and records look up the built-in model, and for which version: the installed one on a
    /// re-run, the requested one on a new install.</summary>
    [Test]
    [Arguments(Given.Default, Recorded.Nothing,  false, Requested)]
    [Arguments(Given.Default, Recorded.Nothing,  true,  Active)]
    [Arguments(Given.Default, Recorded.Builtin,  true,  Active)]
    [Arguments(Given.Default, Recorded.Ollama,   true,  null)]
    [Arguments(Given.Default, Recorded.Declined, true,  null)]
    [Arguments(Given.Builtin, Recorded.Ollama,   true,  Active)]
    [Arguments(Given.Builtin, Recorded.Nothing,  false, Requested)]
    [Arguments(Given.Ollama,  Recorded.Nothing,  false, null)]
    [Arguments(Given.Skip,    Recorded.Nothing,  false, null)]
    public async Task The_builtin_model_is_looked_up_only_when_the_answer_may_use_it(Given given, Recorded recorded, bool reuse, string? fetched) {
        var home = new UserHome(Tmp.CreateDir("home"));
        var root = LocalRoot.Resolve(Tmp.PathTo("root"), home);
        root.EnsureCreated();

        switch (recorded) {
            case Recorded.Builtin:  LocalEmbeddingFile.Write(root, LocalEmbeddingSetting.Builtin(new string('a', 64))); break;
            case Recorded.Ollama:   LocalEmbeddingFile.Write(root, LocalEmbeddingSetting.Ollama("mxbai-embed-large")); break;
            case Recorded.Declined: LocalEmbeddingFile.Write(root, LocalEmbeddingSetting.Declined); break;
        }

        using var bundle    = new FakeBundle(Tmp.PathTo("bundle.tar.gz"), Active);
        var       installer = new LocalInstaller(root, new ConfigRoot(Tmp.PathTo("cfg")), home, new FakeLocalServerService([], home), bundle.Client(),
            new HttpClient(), bundle.Key, new LocalPlatform(true, false, false, Architecture.Arm64), TimeProvider.System, _ => []);
        var plan = reuse
            ? new LocalPlan(PlanHint.Reuse, Requested, new LocalInstallation("abc", 9473, "http://localhost:9473", Active))
            : new LocalPlan(PlanHint.Install, Requested, null);
        LocalEmbeddingAnswer answer = given switch {
            Given.Default => new LocalEmbeddingAnswer.Default(),
            Given.Builtin => new LocalEmbeddingAnswer.Builtin(),
            Given.Ollama  => new LocalEmbeddingAnswer.Ollama("mxbai-embed-large"),
            _             => new LocalEmbeddingAnswer.Skip(),
        };

        await LocalSetupTerminal.BuiltinModelAsync(installer, plan, answer, CancellationToken.None);

        var manifests = bundle.Requests.Where(r => r.EndsWith(".json", StringComparison.Ordinal)).ToList();
        await Assert.That(manifests.Count).IsEqualTo(fetched is null ? 0 : 1);
        if (fetched is not null) await Assert.That(manifests[0]).Contains($"kcap-local-{fetched}-");
    }

    [Test]
    public async Task An_invalid_bundle_version_is_refused_as_the_install_would_refuse_it() {
        var home = new UserHome(Tmp.CreateDir("home"));
        var root = LocalRoot.Resolve(Tmp.PathTo("root"), home);
        using var bundle    = new FakeBundle(Tmp.PathTo("bundle.tar.gz"), Active);
        var       installer = new LocalInstaller(root, new ConfigRoot(Tmp.PathTo("cfg")), home, new FakeLocalServerService([], home), bundle.Client(),
            new HttpClient(), bundle.Key, new LocalPlatform(true, false, false, Architecture.Arm64), TimeProvider.System, _ => []);

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() =>
            LocalSetupTerminal.BuiltinModelAsync(installer, new LocalPlan(PlanHint.Install, "not a version", null), new LocalEmbeddingAnswer.Default(),
                CancellationToken.None));

        await Assert.That(failure!.Code).IsEqualTo(LocalInstallFailureCode.InvalidArguments);
        await Assert.That(LocalSetupJsonLines.Exit(failure.Code)).IsEqualTo(SetupExit.Refused);
        await Assert.That(bundle.Requests).IsEmpty();
    }
}
