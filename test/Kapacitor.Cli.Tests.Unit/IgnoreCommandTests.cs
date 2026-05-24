using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Config;

namespace Kapacitor.Cli.Tests.Unit;

public class IgnoreCommandTests {
    [Test]
    public async Task ApplyAdd_appends_normalized_path_to_excluded_paths() {
        using var tmp     = TempDir.Create();
        var       profile = new Profile();

        var updated = IgnoreCommand.ApplyAdd(profile, tmp.Path);

        await Assert.That(updated.ExcludedPaths).Contains(tmp.Path);
    }

    [Test]
    public async Task ApplyAdd_does_not_duplicate_existing_entry() {
        using var tmp     = TempDir.Create();
        var       profile = new Profile { ExcludedPaths = [tmp.Path] };

        var updated = IgnoreCommand.ApplyAdd(profile, tmp.Path);

        await Assert.That(updated.ExcludedPaths).HasCount().EqualTo(1);
    }

    [Test]
    public async Task ApplyAdd_dedupes_equivalent_path_with_trailing_separator() {
        using var tmp     = TempDir.Create();
        var       profile = new Profile { ExcludedPaths = [tmp.Path] };

        var updated = IgnoreCommand.ApplyAdd(profile, tmp.Path + Path.DirectorySeparatorChar);

        await Assert.That(updated.ExcludedPaths).HasCount().EqualTo(1);
    }

    [Test]
    public async Task ApplyRemove_removes_matching_entry() {
        using var tmp     = TempDir.Create();
        var       profile = new Profile { ExcludedPaths = [tmp.Path] };

        var updated = IgnoreCommand.ApplyRemove(profile, tmp.Path);

        await Assert.That(updated.ExcludedPaths).IsEmpty();
    }

    [Test]
    public async Task ApplyRemove_is_noop_when_path_absent() {
        using var tmp     = TempDir.Create();
        var       profile = new Profile { ExcludedPaths = [tmp.Path] };

        var updated = IgnoreCommand.ApplyRemove(profile, "/some/other/path");

        await Assert.That(updated.ExcludedPaths).HasCount().EqualTo(1);
    }

    [Test]
    public async Task ResolveTargetProfile_prefers_resolved_profile_over_global_active() {
        // When the active resolution picked a non-default profile (e.g. via KAPACITOR_PROFILE,
        // .kapacitor.json, remote match, or `kapacitor use` binding), ignore must write to
        // the same profile the hook will read from in the same cwd.
        var config = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> {
                ["default"]    = new(),
                ["consulting"] = new(),
            }
        };

        var target = IgnoreCommand.ResolveTargetProfile(config, resolvedProfileName: "consulting");

        await Assert.That(target).IsEqualTo("consulting");
    }

    [Test]
    public async Task ResolveTargetProfile_falls_back_to_active_when_unresolved() {
        var config = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> { ["default"] = new() }
        };

        var target = IgnoreCommand.ResolveTargetProfile(config, resolvedProfileName: null);

        await Assert.That(target).IsEqualTo("default");
    }

    [Test]
    public async Task ShouldExclude_excludes_when_path_excluded_even_if_repo_included() {
        // Regression: when a session matches BOTH an excluded repo and an excluded path,
        // opting-in to the repo must not bypass the path exclusion.
        var c = MakeClassification(excludedRepoKey: "acme/secret", excludedPathKey: "/home/alice/secret-dir");
        var includedRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acme/secret" };
        var includedPaths = new HashSet<string>(StringComparer.Ordinal);

        await Assert.That(HistoryCommand.ShouldExclude(c, includedRepos, includedPaths)).IsTrue();
    }

    [Test]
    public async Task ShouldExclude_excludes_when_repo_excluded_even_if_path_included() {
        var c = MakeClassification(excludedRepoKey: "acme/secret", excludedPathKey: "/home/alice/secret-dir");
        var includedRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includedPaths = new HashSet<string>(StringComparer.Ordinal) { "/home/alice/secret-dir" };

        await Assert.That(HistoryCommand.ShouldExclude(c, includedRepos, includedPaths)).IsTrue();
    }

    [Test]
    public async Task ShouldExclude_includes_when_both_keys_opted_in() {
        var c = MakeClassification(excludedRepoKey: "acme/secret", excludedPathKey: "/home/alice/secret-dir");
        var includedRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acme/secret" };
        var includedPaths = new HashSet<string>(StringComparer.Ordinal) { "/home/alice/secret-dir" };

        await Assert.That(HistoryCommand.ShouldExclude(c, includedRepos, includedPaths)).IsFalse();
    }

    [Test]
    public async Task ShouldExclude_returns_false_when_no_exclusion_keys() {
        var c = MakeClassification();

        await Assert.That(HistoryCommand.ShouldExclude(c,
                              new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                              new HashSet<string>(StringComparer.Ordinal))).IsFalse();
    }

    static HistoryCommand.SessionClassification MakeClassification(
            string? excludedRepoKey = null,
            string? excludedPathKey = null
        ) => new() {
        SessionId       = "abc",
        FilePath        = "/tmp/transcript.jsonl",
        EncodedCwd      = "-",
        Meta            = new SessionMetadata(),
        Status          = HistoryCommand.ClassificationStatus.New,
        ExcludedRepoKey = excludedRepoKey,
        ExcludedPathKey = excludedPathKey,
    };

    [Test]
    public async Task ApplyRemove_matches_with_trailing_separator() {
        using var tmp     = TempDir.Create();
        var       profile = new Profile { ExcludedPaths = [tmp.Path] };

        var updated = IgnoreCommand.ApplyRemove(profile, tmp.Path + Path.DirectorySeparatorChar);

        await Assert.That(updated.ExcludedPaths).IsEmpty();
    }
}
