using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportHandoffFileTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    const string RunId = "0123456789abcdef0123456789abcdef";

    static ForegroundImportOutcome Outcome(int candidates = 7, params string[] succeeded) => new(
        ForegroundCertainty.Complete, 5, succeeded.Length, 0, 0, RemainderExists: true,
        Enumerable.Range(0, candidates).Select(i => $"c{i:000}").ToList(), succeeded);

    static ImportHandoffFile Compose(ForegroundImportOutcome o, bool offered = true, HandoffSuppressedReason? reason = null) =>
        ImportHandoffFile.Compose(RunId, Now, offered, reason, o, new(BackgroundImportStatus.Running, "/tmp/import-x.log", null, null),
            "https://acme.kcap.ai/", "work", unattributedOnDisk: 1552);

    [Test]
    public async Task Json_has_the_pinned_shape() {
        var json = JsonNode.Parse(Compose(Outcome(3, "c000")).ToJson())!.AsObject();

        await Assert.That(json["schema_version"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(json["run_id"]!.GetValue<string>()).IsEqualTo(RunId);
        await Assert.That(json["written_at"]!.GetValue<string>()).IsEqualTo("2026-09-17T12:00:00+00:00");
        await Assert.That(json["handoff_offered"]!.GetValue<bool>()).IsTrue();
        await Assert.That(json["handoff_suppressed"]).IsNull();
        await Assert.That(json["foreground_certainty"]!.GetValue<string>()).IsEqualTo("complete");
        await Assert.That(json["server_url"]!.GetValue<string>()).IsEqualTo("https://acme.kcap.ai");
        await Assert.That(json["profile"]!.GetValue<string>()).IsEqualTo("work");
        await Assert.That(json["scope"]!.GetValue<string>()).IsEqualTo("all");
        await Assert.That(json["cohort"]!.GetValue<string>()).IsEqualTo("exact");
        await Assert.That(json["session_ids"]!.AsArray().Count).IsEqualTo(3);
        await Assert.That(json["foreground_succeeded_ids"]!.AsArray()[0]!.GetValue<string>()).IsEqualTo("c000");
        await Assert.That(json["unattributed_on_disk"]!.GetValue<int>()).IsEqualTo(1552);
        await Assert.That(json["background"]!.GetValue<string>()).IsEqualTo("running");
        await Assert.That(json["background_log"]!.GetValue<string>()).IsEqualTo("/tmp/import-x.log");
    }

    [Test]
    public async Task More_than_500_candidates_keeps_the_first_500_as_partial_exact() {
        var file = Compose(Outcome(candidates: 600));

        await Assert.That(file.Cohort).IsEqualTo(HandoffCohort.PartialExact);
        await Assert.That(file.SessionIds.Count).IsEqualTo(500);
        await Assert.That(file.SessionIds[0]).IsEqualTo("c000");
    }

    [Test]
    public async Task Unknown_candidates_write_an_unknown_cohort_with_no_ids() {
        var o = new ForegroundImportOutcome(ForegroundCertainty.Incomplete, 0, 0, 0, 0, true, null, []);

        var file = Compose(o, offered: false, reason: HandoffSuppressedReason.ImportFailed);
        var json = JsonNode.Parse(file.ToJson())!.AsObject();

        await Assert.That(json["cohort"]!.GetValue<string>()).IsEqualTo("unknown");
        await Assert.That(json["session_ids"]!.AsArray()).IsEmpty();
        await Assert.That(json["handoff_suppressed"]!.GetValue<string>()).IsEqualTo("import_failed");
    }

    [Test]
    public async Task Write_publishes_owner_only_and_prunes_files_older_than_seven_days() {
        var stale = Config.Root.Path("import-handoff-ffffffffffffffffffffffffffffffff.json");
        await File.WriteAllTextAsync(stale, "{}");
        File.SetLastWriteTimeUtc(stale, Now.AddDays(-8).UtcDateTime);
        var fresh = Config.Root.Path("import-handoff-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee.json");
        await File.WriteAllTextAsync(fresh, "{}");
        File.SetLastWriteTimeUtc(fresh, Now.AddDays(-1).UtcDateTime);

        Compose(Outcome()).Write(Config.Root, new FixedTime(Now));

        var path = ImportHandoffFile.PathFor(Config.Root, RunId);
        await Assert.That(File.Exists(path)).IsTrue();
        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(File.Exists(stale)).IsFalse();
        await Assert.That(File.Exists(fresh)).IsTrue();
        await Assert.That(Directory.GetFiles(Config.Directory, "*.tmp")).IsEmpty();
    }

    [Test]
    public async Task Write_refuses_a_pre_existing_final_path_and_leaves_it_untouched() {
        var path = ImportHandoffFile.PathFor(Config.Root, RunId);
        await File.WriteAllTextAsync(path, "someone else's");

        await Assert.That(() => Compose(Outcome()).Write(Config.Root, new FixedTime(Now))).Throws<IOException>();
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("someone else's");
        await Assert.That(Directory.GetFiles(Config.Directory, "*.tmp")).IsEmpty();
    }

    sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
