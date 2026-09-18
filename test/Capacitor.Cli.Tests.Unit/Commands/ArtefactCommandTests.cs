using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

[NotInParallel]
public class ArtefactCommandTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Page() => Tmp.CreateFile("plan.html", "<h1>plan</h1>");

    [Test]
    public async Task An_update_without_an_id_is_a_usage_error_not_a_new_artefact() {
        var api = new RecordingArtefactsApi();

        using var console = ConsoleOutput.StartFullCapture();
        var exit = await new ArtefactCommand(api).HandleAsync(["artefact", "publish", Page(), "--update"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(api.Calls).IsEmpty();
        await Assert.That(console.GetCapturedError()).Contains("--update needs a value");
    }

    [Test]
    public async Task An_update_with_an_id_publishes_a_version_of_that_artefact() {
        var api = new RecordingArtefactsApi();

        using var console = ConsoleOutput.StartFullCapture();
        var exit = await new ArtefactCommand(api).HandleAsync(["artefact", "publish", Page(), "--update", "a1"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(api.Calls).IsEquivalentTo(new[] { "version:a1" });
    }

    [Test]
    [Arguments("--to")]
    [Arguments("--visibility")]
    public async Task A_share_flag_without_its_value_changes_nothing(string flag) {
        var api = new RecordingArtefactsApi();

        // --visibility scoped comes first so the bare flag under test is the last token either way.
        string[] args = flag == "--to"
            ? ["artefact", "share", "a1", "--visibility", "scoped", "--to"]
            : ["artefact", "share", "a1", "--to", "team:platform", "--visibility"];

        using var console = ConsoleOutput.StartFullCapture();
        var exit = await new ArtefactCommand(api).HandleAsync(args);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(api.Calls).IsEmpty();
        await Assert.That(console.GetCapturedError()).Contains($"{flag} needs a value");
    }

    [Test]
    public async Task Sharing_as_scoped_with_nobody_sends_an_explicit_empty_audience() {
        var api = new RecordingArtefactsApi();

        using var console = ConsoleOutput.StartFullCapture();
        var exit = await new ArtefactCommand(api).HandleAsync(["artefact", "share", "a1", "--visibility", "scoped"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(api.SharedGrants).IsNotNull();
        await Assert.That(api.SharedGrants!.Count).IsEqualTo(0);
    }

    sealed class RecordingArtefactsApi : IArtefactsApi {
        public List<string> Calls { get; } = [];

        public List<ArtefactGrantDto>? SharedGrants { get; private set; }

        static ArtefactWriteResult Written(string id) =>
            new ArtefactWriteResult.Written(new(
                new(id, "Plan", "u1", "none", 1, DateTimeOffset.UnixEpoch, true, $"https://kcap.test/artefacts/{id}"), null));

        public Task<ArtefactWriteResult> PublishAsync(PublishArtefactBody body, CancellationToken ct = default) {
            Calls.Add("publish");
            return Task.FromResult(Written("new"));
        }

        public Task<ArtefactWriteResult> PublishVersionAsync(string artefactId, string html, CancellationToken ct = default) {
            Calls.Add($"version:{artefactId}");
            return Task.FromResult(Written(artefactId));
        }

        public Task<ArtefactWriteResult> SetVisibilityAsync(string artefactId, string visibility,
                                                            List<ArtefactGrantDto>? grants, CancellationToken ct = default) {
            Calls.Add($"share:{artefactId}");
            SharedGrants = grants;
            return Task.FromResult(Written(artefactId));
        }

        public Task<ArtefactWriteResult> DeleteAsync(string artefactId, CancellationToken ct = default) {
            Calls.Add($"delete:{artefactId}");
            return Task.FromResult<ArtefactWriteResult>(new ArtefactWriteResult.Gone());
        }

        public Task<List<ArtefactDto>> ListAsync(CancellationToken ct = default) {
            Calls.Add("list");
            return Task.FromResult(new List<ArtefactDto>());
        }
    }
}
