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

    [Test]
    public async Task A_flag_the_command_does_not_read_is_a_usage_error() {
        var api = new RecordingArtefactsApi();

        using var console = ConsoleOutput.StartFullCapture();
        var exit = await new ArtefactCommand(api).HandleAsync(["artefact", "share", "a1", "--visibility", "org", "--title", "New"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(api.Calls).IsEmpty();
        await Assert.That(console.GetCapturedError()).Contains("--title is not a flag of this command");
    }

    [Test]
    public async Task Get_prints_a_shared_pages_text_to_stdout_and_says_whose_words_they_are() {
        var api = new RecordingArtefactsApi();

        using var console = ConsoleOutput.StartFullCapture();
        var exit = await new ArtefactCommand(api).HandleAsync(["page", "get", "https://kcap.test/pages/a1"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(api.Calls).IsEquivalentTo(new[] { "read:a1@latest" });
        await Assert.That(console.GetCapturedOutput().Trim()).IsEqualTo("Shared plan");
        await Assert.That(console.GetCapturedError()).Contains("Written by u2");
    }

    [Test]
    public async Task Html_ahead_of_the_id_is_a_switch_and_leaves_the_id_alone() {
        var api = new RecordingArtefactsApi();

        using var console = ConsoleOutput.StartFullCapture();
        var exit = await new ArtefactCommand(api).HandleAsync(["page", "get", "--html", "a1", "--version", "3"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(api.Calls).IsEquivalentTo(new[] { "read:a1@3" });
        await Assert.That(console.GetCapturedOutput().Trim()).IsEqualTo(RecordingArtefactsApi.SharedHtml);
    }

    [Test]
    public async Task Get_of_a_page_the_profile_may_not_open_fails() {
        var api = new RecordingArtefactsApi();

        using var console = ConsoleOutput.StartFullCapture();
        var exit = await new ArtefactCommand(api).HandleAsync(["page", "get", "hidden"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(console.GetCapturedOutput()).IsEmpty();
        await Assert.That(console.GetCapturedError()).Contains("not visible to this profile");
    }

    [Test]
    public async Task A_terminal_is_shown_a_pages_control_characters_rather_than_sent_them() =>
        await Assert.That(ArtefactCommand.ForTerminal("ok\u001b[2Jcleared\r\nnext\tcell"))
                    .IsEqualTo("ok\\u001b[2Jcleared\\u000d\nnext\tcell");

    static PageSearchHitDto Hit(string id, string title, string excerpt = "", string? name = "Ada Lovelace",
                                bool mine = false) =>
        new(id, title, "github:42", name, "org", 2, new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero),
            mine, $"https://kcap.test/pages/{id}", excerpt, "body");

    static async Task<(int Exit, string Out, string Err)> SearchAsync(RecordingArtefactsApi api, params string[] args) {
        using var console = ConsoleOutput.StartFullCapture();
        var exit = await new ArtefactCommand(api).HandleAsync(["page", "search", .. args]);

        return (exit, console.GetCapturedOutput(), console.GetCapturedError());
    }

    [Test]
    public async Task Search_prints_the_list_table_with_an_author_column_and_excerpt_lines() {
        var api = new RecordingArtefactsApi {
            SearchResult = new ArtefactSearchResult.Found([
                Hit("p1", "Ferry timetable", excerpt: "harbour departures", mine: true),
                Hit("p2", "Harbour schedule", name: "Grace Hopper"),
                Hit("p3", "Untitled", name: null)
            ])
        };

        var (exit, output, _) = await SearchAsync(api, "ferry");

        var lines = output.TrimEnd().Split(Environment.NewLine);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(lines).Count().IsEqualTo(5);
        await Assert.That(lines[0]).IsEqualTo("PAGE  VERSION VISIBILITY UPDATED     AUTHOR        TITLE");
        await Assert.That(lines[1]).IsEqualTo("p1    v2      org        2026-09-16  you           Ferry timetable");
        await Assert.That(lines[2]).IsEqualTo("    harbour departures");
        await Assert.That(lines[3]).IsEqualTo("p2    v2      org        2026-09-16  Grace Hopper  Harbour schedule");
        await Assert.That(lines[4]).IsEqualTo("p3    v2      org        2026-09-16  github:42     Untitled");
    }

    [Test]
    public async Task Search_neutralises_control_characters_in_author_text() {
        var api = new RecordingArtefactsApi {
            SearchResult = new ArtefactSearchResult.Found([Hit("p1", "Q3\u001b[31m", excerpt: "a\nb", name: "Eve\r\nRoot")])
        };

        var (_, output, _) = await SearchAsync(api, "q3");

        await Assert.That(output).DoesNotContain("\u001b");
        await Assert.That(output).Contains("a b");
        await Assert.That(output).Contains("Eve Root");
        await Assert.That(output.TrimEnd().Split(Environment.NewLine)).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Search_joins_positionals_and_passes_the_limit() {
        var api = new RecordingArtefactsApi();

        await SearchAsync(api, "ferry", "timetable", "--limit", "5");
        await SearchAsync(api, "ferry");

        await Assert.That(api.Calls).IsEquivalentTo(new[] { "search:ferry timetable:5", "search:ferry:20" });
    }

    [Test]
    public async Task Search_with_no_hits_says_so_and_exits_0() {
        var (exit, output, _) = await SearchAsync(new RecordingArtefactsApi(), "ferry");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output.Trim()).IsEqualTo("No pages match \"ferry\".");
    }

    [Test]
    public async Task Search_against_an_older_server_exits_1() {
        var api = new RecordingArtefactsApi { SearchResult = new ArtefactSearchResult.ServerTooOld() };

        var (exit, output, error) = await SearchAsync(api, "ferry");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output).IsEmpty();
        await Assert.That(error.Trim()).IsEqualTo("This server does not support page search yet; it needs a newer kcap server.");
    }

    [Test]
    [Arguments("")]
    [Arguments("--limit 5")]
    [Arguments("ferry --limit many")]
    public async Task Search_without_a_query_or_with_a_bad_limit_is_a_usage_error(string args) {
        var api = new RecordingArtefactsApi();

        var (exit, _, error) = await SearchAsync(api, args.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(api.Calls).IsEmpty();
        await Assert.That(error).Contains("kcap page search");
    }

    sealed class RecordingArtefactsApi : IArtefactsApi {
        public const string SharedHtml = "<h1>Shared plan</h1><script>steal()</script>";

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

        public ArtefactSearchResult SearchResult { get; init; } = new ArtefactSearchResult.Found([]);

        public Task<ArtefactSearchResult> SearchAsync(string query, int limit, CancellationToken ct = default) {
            Calls.Add($"search:{query}:{limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return Task.FromResult(SearchResult);
        }

        public Task<ArtefactReadResult> ReadAsync(string artefactId, int? version, CancellationToken ct = default) {
            Calls.Add($"read:{artefactId}@{(version is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "latest")}");

            return Task.FromResult<ArtefactReadResult>(artefactId == "hidden"
                ? new ArtefactReadResult.NotFound()
                : new ArtefactReadResult.Read(
                    new(artefactId, "Plan", "u2", "org", 3, DateTimeOffset.UnixEpoch, false, $"https://kcap.test/pages/{artefactId}"),
                    version ?? 3, SharedHtml));
        }
    }
}
