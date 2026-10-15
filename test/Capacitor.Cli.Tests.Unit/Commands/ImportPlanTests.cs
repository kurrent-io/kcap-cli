using Capacitor.Cli.Commands;
using TUnit.Assertions.Enums;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportPlanTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static ImportPlanLevel Level(FirstRunImportLevel level, params string[] slugs) => new(
        level,
        [.. slugs.Select(s => s.Split('/')).Select(p => new FirstRunImportChoice(p[0], p[1], level))],
        null, null, false);

    [Test]
    public async Task Round_trips_levels_in_order() {
        var plan = new ImportPlan("https://new.example", [
            new ImportPlanLevel(FirstRunImportLevel.OnlyMe, [new("acme", "secret", FirstRunImportLevel.OnlyMe)],
                new DateOnly(2026, 7, 10), [HarnessId.Claude, HarnessId.OpenCode], true),
            Level(FirstRunImportLevel.Shared, "acme/api", "acme/web"),
        ]);

        var back = ImportPlan.Parse(plan.ToJson())!;

        await Assert.That(back.ServerUrl).IsEqualTo("https://new.example");
        await Assert.That(back.Levels.Select(l => l.Level)).IsEquivalentTo([FirstRunImportLevel.OnlyMe, FirstRunImportLevel.Shared], CollectionOrdering.Matching);
        await Assert.That(back.Levels[0].Repos.Select(r => r.Slug)).IsEquivalentTo(["acme/secret"]);
        await Assert.That(back.Levels[0].Repos[0].Level).IsEqualTo(FirstRunImportLevel.OnlyMe);
        await Assert.That(back.Levels[0].Since).IsEqualTo(new DateOnly(2026, 7, 10));
        await Assert.That(back.Levels[0].Vendors!).IsEquivalentTo([HarnessId.Claude, HarnessId.OpenCode]);
        await Assert.That(back.Levels[0].SkipTitle).IsTrue();
        await Assert.That(back.Levels[1].Repos.Select(r => r.Slug)).IsEquivalentTo(["acme/api", "acme/web"], CollectionOrdering.Matching);
        await Assert.That(back.Levels[1].Repos[1].Level).IsEqualTo(FirstRunImportLevel.Shared);
        await Assert.That(back.Levels[1].Since).IsNull();
        await Assert.That(back.Levels[1].Vendors).IsNull();
        await Assert.That(back.Levels[1].SkipTitle).IsFalse();
    }

    [Test]
    public async Task The_wire_shape_uses_snake_case_level_tokens_and_slugs() {
        var json = new ImportPlan("https://x.example", [Level(FirstRunImportLevel.OnlyMe, "a/b")]).ToJson();

        await Assert.That(json).Contains("\"schema_version\": 1");
        await Assert.That(json).Contains("\"level\": \"only_me\"");
        await Assert.That(json).Contains("\"a/b\"");
    }

    [Test]
    public async Task Parse_rejects_missing_server() {
        await Assert.That(ImportPlan.Parse("""{"schema_version":1,"levels":[{"level":"shared","repos":["a/b"],"since":null,"vendors":null,"skip_title":false}]}""")).IsNull();
        await Assert.That(ImportPlan.Parse("""{"schema_version":1,"server_url":"","levels":[{"level":"shared","repos":["a/b"],"since":null,"vendors":null,"skip_title":false}]}""")).IsNull();
    }

    [Test]
    public async Task Parse_rejects_unknown_level() {
        await Assert.That(ImportPlan.Parse("""{"schema_version":1,"server_url":"https://x.example","levels":[{"level":"everyone","repos":["a/b"],"since":null,"vendors":null,"skip_title":false}]}""")).IsNull();
    }

    [Test]
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("""{"schema_version":2,"server_url":"https://x.example","levels":[{"level":"shared","repos":["a/b"],"since":null,"vendors":null,"skip_title":false}]}""")]
    [Arguments("""{"schema_version":1,"server_url":"https://x.example","levels":[]}""")]
    [Arguments("""{"schema_version":1,"server_url":"https://x.example","levels":[{"level":"shared","repos":[],"since":null,"vendors":null,"skip_title":false}]}""")]
    [Arguments("""{"schema_version":1,"server_url":"https://x.example","levels":[{"level":"shared","repos":["nobody"],"since":null,"vendors":null,"skip_title":false}]}""")]
    [Arguments("""{"schema_version":1,"server_url":"https://x.example","levels":[{"level":"shared","repos":["a/b"],"since":"July","vendors":null,"skip_title":false}]}""")]
    [Arguments("""{"schema_version":1,"server_url":"https://x.example","levels":[{"level":"shared","repos":["a/b"],"since":null,"vendors":["nope"],"skip_title":false}]}""")]
    [Arguments("""{"schema_version":1,"server_url":"https://x.example","levels":[{"level":"shared","repos":["a/b"],"since":null,"vendors":null}]}""")]
    public async Task Parse_rejects_malformed_plans(string json) {
        await Assert.That(ImportPlan.Parse(json)).IsNull();
    }

    [Test]
    public async Task Read_is_null_for_a_missing_file() {
        await Assert.That(ImportPlan.Read(Config.PathTo("import-plan-none.json"))).IsNull();
    }

    [Test]
    [Arguments("https://kcap.example", true)]
    [Arguments("https://kcap.example:8443/base", true)]
    [Arguments("http://localhost:5000", true)]
    [Arguments("http://127.0.0.1:5000", true)]
    [Arguments("http://[::1]:5000", true)]
    [Arguments("http://kcap.example", false)]
    [Arguments("ftp://kcap.example", false)]
    [Arguments("kcap.example", false)]
    [Arguments("/relative", false)]
    [Arguments("", false)]
    public async Task IsUsableServer_accepts_https_and_loopback_http_only(string url, bool usable) {
        await Assert.That(ImportPlan.IsUsableServer(url)).IsEqualTo(usable);
    }

    [Test]
    public async Task Write_creates_an_owner_only_file() {
        if (OperatingSystem.IsWindows()) return;
        var path = ImportPlan.PathFor(Config.Root, "run1");
        var plan = new ImportPlan("https://x.example", [Level(FirstRunImportLevel.Shared, "a/b")]);

        plan.Write(path);

        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(ImportPlan.Read(path)!.ServerUrl).IsEqualTo("https://x.example");
    }

    [Test]
    public async Task Write_refuses_an_existing_path() {
        var path = Config.CreateFile("import-plan-run1.json", "someone else's");
        var plan = new ImportPlan("https://x.example", [Level(FirstRunImportLevel.Shared, "a/b")]);

        await Assert.That(() => plan.Write(path)).Throws<IOException>();
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("someone else's");
    }

    [Test]
    public async Task Prune_removes_only_old_plans() {
        var now   = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var old   = Config.CreateFile("import-plan-old.json", "{}");
        var fresh = Config.CreateFile("import-plan-fresh.json", "{}");
        var other = Config.CreateFile("import-handoff-old.json", "{}");
        File.SetLastWriteTimeUtc(old,   now.UtcDateTime.AddDays(-8));
        File.SetLastWriteTimeUtc(fresh, now.UtcDateTime.AddDays(-6));
        File.SetLastWriteTimeUtc(other, now.UtcDateTime.AddDays(-8));

        ImportPlan.Prune(Config.Root, now);

        await Assert.That(File.Exists(old)).IsFalse();
        await Assert.That(File.Exists(fresh)).IsTrue();
        await Assert.That(File.Exists(other)).IsTrue();
    }
}
