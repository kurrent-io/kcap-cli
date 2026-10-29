using TUnit.Core.Enums;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

[ExcludeOn(OS.Windows)]
public class LocalClientCopiesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class FakeRunner(Dictionary<string, ProcessResult> answers) : IProcessRunner {
        public List<string> Ran { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Ran.Add($"{fileName} {string.Join(' ', args)}");

            return Task.FromResult(answers.TryGetValue(fileName, out var answer) ? answer : new ProcessResult(127, "", "not found", false));
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine,
            CancellationToken ct) => throw new NotSupportedException();
    }

    static ProcessResult Prints(string version) => new(0, $"kcap {version}\n", "", false);

    string Kcap(string dir) => Tmp.CreateExecutable($"{dir}/kcap", "#!/bin/sh\nexit 0\n");

    [Test]
    public async Task Finds_the_kcap_on_path_and_the_desktop_apps_with_their_versions() {
        var onPath = Kcap("bin");
        var app    = Kcap("kcap.app/Contents/MacOS");
        var runner = new FakeRunner(new() { [onPath] = Prints("1.2.0"), [app] = Prints("1.3.0+abc") });

        var copies = new LocalClientCopies(runner, Tmp.PathTo("bin"), self: null, [app, Tmp.PathTo("Elsewhere.app/Contents/MacOS/kcap")]).Find();

        await Assert.That(copies).IsEquivalentTo([
            new ClientCopy(ClientCopyKind.OnPath, onPath, "1.2.0"),
            new ClientCopy(ClientCopyKind.DesktopApp, app, "1.3.0+abc"),
        ]);
        await Assert.That(runner.Ran).Contains($"{onPath} --version --no-update-check");
    }

    [Test]
    public async Task The_running_kcap_is_not_asked_even_through_a_link() {
        var self = Kcap("real");
        Tmp.CreateDir("bin");
        File.CreateSymbolicLink(Tmp.PathTo("bin", "kcap"), self);
        var runner = new FakeRunner([]);

        var copies = new LocalClientCopies(runner, Tmp.PathTo("bin"), self, []).Find();

        await Assert.That(copies).IsEmpty();
        await Assert.That(runner.Ran).IsEmpty();
    }

    [Test]
    public async Task A_path_link_to_the_desktop_apps_kcap_is_one_copy_of_the_app() {
        var app = Kcap("kcap.app/Contents/MacOS");
        Tmp.CreateDir("bin");
        File.CreateSymbolicLink(Tmp.PathTo("bin", "kcap"), app);
        var runner = new FakeRunner(new() { [app] = Prints("1.2.0") });

        var copies = new LocalClientCopies(runner, Tmp.PathTo("bin"), self: null, [app]).Find();

        await Assert.That(copies).IsEquivalentTo([new ClientCopy(ClientCopyKind.DesktopApp, app, "1.2.0")]);
    }

    [Test]
    [MethodDataSource(nameof(Unreadable))]
    public async Task A_copy_that_cannot_report_its_version_has_none(ProcessResult answer) {
        var onPath = Kcap("bin");

        var copies = new LocalClientCopies(new FakeRunner(new() { [onPath] = answer }), Tmp.PathTo("bin"), self: null, []).Find();

        await Assert.That(copies).IsEquivalentTo([new ClientCopy(ClientCopyKind.OnPath, onPath, null)]);
    }

    public static IEnumerable<Func<ProcessResult>> Unreadable() {
        yield return () => new(1, "kcap 1.3.0\n", "", false);
        yield return () => new(0, "kcap 1.3.0\n", "", true);
        yield return () => new(0, "Platform package @kurrent/kcap-darwin-arm64 is not installed\n", "", false);
        yield return () => new(0, "kcap unknown\n", "", false);
        yield return () => new(0, "kcap 1.3.0\nkcap 1.2.0\n", "", false);
    }

    [Test]
    public async Task Asks_each_copy_once() {
        var onPath = Kcap("bin");
        var runner = new FakeRunner(new() { [onPath] = Prints("1.3.0") });
        var copies = new LocalClientCopies(runner, Tmp.PathTo("bin"), self: null, []);

        copies.Find();
        copies.Find();

        await Assert.That(runner.Ran.Count).IsEqualTo(1);
    }

    [Test]
    [ParallelLimiter<SubprocessLimit>]
    public async Task Reads_the_version_a_real_copy_prints() {
        Tmp.CreateExecutable("bin/kcap", "#!/bin/sh\necho \"kcap 1.2.0\"\n");

        var copies = new LocalClientCopies(new ProcessRunner(TimeProvider.System), Tmp.PathTo("bin"), self: null, []).Find();

        await Assert.That(copies.Single().Version).IsEqualTo("1.2.0");
    }
}
