using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SessionsArgsTests {
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 28, 15, 0, 0, TimeSpan.Zero));

    [Test]
    public async Task Defaults_are_active_current_repo_everyone_limit_twenty_table() {
        var opts = SessionsArgs.Parse(["sessions"], Clock, out var error);

        await Assert.That(error).IsNull();
        await Assert.That(opts!.State).IsEqualTo("active");
        await Assert.That(opts.Repo).IsNull();
        await Assert.That(opts.Mine).IsFalse();
        await Assert.That(opts.Touching).IsNull();
        await Assert.That(opts.Limit).IsEqualTo(20);
        await Assert.That(opts.Json).IsFalse();
    }

    [Test]
    public async Task All_flags_parse() {
        var opts = SessionsArgs.Parse(["sessions", "--ended", "--repo", "acme/widgets", "--mine", "--touching", "src/Foo", "--limit", "5", "--json"], Clock, out var error);

        await Assert.That(error).IsNull();
        await Assert.That(opts!.State).IsEqualTo("ended");
        await Assert.That(opts.Repo).IsEqualTo("acme/widgets");
        await Assert.That(opts.RepoHash).IsEqualTo(RepoHashHelper.ComputeRepoHash("acme", "widgets"));
        await Assert.That(opts.Mine).IsTrue();
        await Assert.That(opts.Touching).IsEqualTo("src/Foo");
        await Assert.That(opts.Limit).IsEqualTo(5);
        await Assert.That(opts.Json).IsTrue();
    }

    [Test]
    public async Task Nested_group_owner_repo_resolves_the_group_path_hash() {
        var opts = SessionsArgs.Parse(["sessions", "--repo", "group/subgroup/project"], Clock, out var error);

        await Assert.That(error).IsNull();
        await Assert.That(opts!.RepoHash).IsEqualTo(RepoHashHelper.ComputeRepoHash("group/subgroup", "project"));
    }

    [Test]
    public async Task Two_state_flags_is_a_usage_error() {
        var opts = SessionsArgs.Parse(["sessions", "--active", "--all"], Clock, out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains("--active");
    }

    [Test]
    [Arguments("owner")]
    [Arguments("DA9C523C68AEE2F1")]
    public async Task Malformed_repo_is_a_usage_error(string repo) {
        var opts = SessionsArgs.Parse(["sessions", "--repo", repo], Clock, out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains("--repo");
    }

    [Test]
    [Arguments("--repo")]
    [Arguments("--touching")]
    [Arguments("--limit")]
    public async Task Value_flag_without_a_value_is_a_usage_error(string flag) {
        var opts = SessionsArgs.Parse(["sessions", flag], Clock, out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains(flag);
    }

    [Test]
    [Arguments("0")]
    [Arguments("101")]
    [Arguments("ten")]
    public async Task Limit_outside_one_to_hundred_is_a_usage_error(string limit) {
        var opts = SessionsArgs.Parse(["sessions", "--limit", limit], Clock, out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains("--limit");
    }

    [Test]
    public async Task Unknown_flag_is_a_usage_error() {
        var opts = SessionsArgs.Parse(["sessions", "--everyone"], Clock, out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains("--everyone");
    }

    [Test]
    public async Task Repo_all_selects_every_repository_and_resolves_no_hash() {
        var opts = SessionsArgs.Parse(["sessions", "--repo", "all"], Clock, out var error);

        await Assert.That(error).IsNull();
        await Assert.That(opts!.AllRepos).IsTrue();
        await Assert.That(opts.Repo).IsEqualTo("all");
        await Assert.That(opts.RepoHash).IsNull();
    }

    [Test]
    public async Task A_period_resolves_to_instants_and_lists_every_state_by_default() {
        var opts = SessionsArgs.Parse(["sessions", "--since", "2026-09-27", "--until", "2026-09-28"], Clock, out var error);

        await Assert.That(error).IsNull();
        await Assert.That(opts!.Since).IsEqualTo(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        await Assert.That(opts.Until).IsEqualTo(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
        await Assert.That(opts.State).IsEqualTo("all");
        await Assert.That(opts.Windowed).IsTrue();
    }

    [Test]
    public async Task A_duration_counts_back_from_the_clock() {
        var opts = SessionsArgs.Parse(["sessions", "--since", "14d"], Clock, out _);

        await Assert.That(opts!.Since).IsEqualTo(new DateTimeOffset(2026, 9, 14, 15, 0, 0, TimeSpan.Zero));
        await Assert.That(opts.Until).IsNull();
    }

    [Test]
    public async Task An_explicit_state_survives_a_period() {
        var opts = SessionsArgs.Parse(["sessions", "--active", "--since", "14d"], Clock, out _);

        await Assert.That(opts!.State).IsEqualTo("active");
    }

    [Test]
    public async Task Without_a_period_the_default_is_still_active() {
        var opts = SessionsArgs.Parse(["sessions", "--repo", "all"], Clock, out _);

        await Assert.That(opts!.State).IsEqualTo("active");
        await Assert.That(opts.Windowed).IsFalse();
    }

    [Test]
    public async Task Since_later_than_until_is_a_usage_error() {
        var opts = SessionsArgs.Parse(["sessions", "--since", "2026-09-28", "--until", "2026-09-27"], Clock, out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains("--since is later than --until");
    }

    [Test]
    public async Task The_same_instant_for_both_bounds_is_accepted() {
        var opts = SessionsArgs.Parse(["sessions", "--since", "2026-09-27T09:00:00Z", "--until", "2026-09-27T09:00:00Z"], Clock, out var error);

        await Assert.That(error).IsNull();
        await Assert.That(opts!.Since).IsEqualTo(opts.Until);
    }

    [Test]
    [Arguments("--since", "yesterday")]
    [Arguments("--until", "2026-09-27T09:00:00")]
    [Arguments("--since", "0d")]
    public async Task A_time_that_cannot_be_read_names_the_flag(string flag, string value) {
        var opts = SessionsArgs.Parse(["sessions", flag, value], Clock, out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains(flag);
    }

    [Test]
    [Arguments("--since")]
    [Arguments("--until")]
    [Arguments("--cursor")]
    public async Task A_new_value_flag_without_a_value_is_a_usage_error(string flag) {
        var opts = SessionsArgs.Parse(["sessions", flag], Clock, out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains(flag);
    }

    [Test]
    public async Task A_cursor_combines_with_repo_limit_and_json() {
        var opts = SessionsArgs.Parse(["sessions", "--repo", "all", "--cursor", "eyJ2IjoxfQ", "--limit", "50", "--json"], Clock, out var error);

        await Assert.That(error).IsNull();
        await Assert.That(opts!.Cursor).IsEqualTo("eyJ2IjoxfQ");
        await Assert.That(opts.Windowed).IsTrue();
        await Assert.That(opts.Limit).IsEqualTo(50);
    }

    /// <summary>The server ignores every filter sent beside a cursor, so accepting one here would
    /// print a page that looks narrowed and is not.</summary>
    [Test]
    [Arguments("--mine")]
    [Arguments("--ended")]
    [Arguments("--touching")]
    [Arguments("--since")]
    [Arguments("--until")]
    public async Task A_cursor_beside_a_filter_is_refused_naming_the_filter(string flag) {
        var filter = flag switch {
            "--touching" => new[] { flag, "src/Foo" },
            "--since"    => new[] { flag, "14d" },
            "--until"    => new[] { flag, "2026-09-28" },
            _            => new[] { flag }
        };

        var opts = SessionsArgs.Parse(["sessions", "--cursor", "eyJ2IjoxfQ", .. filter], Clock, out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains("--cursor");
        await Assert.That(error).Contains(flag);
    }
}
