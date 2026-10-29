using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class IgnoreCommandTests {
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task ApplyAdd_appends_normalized_path_to_excluded_paths() {
        using var tmp     = new TempDir();
        var       profile = new Profile();

        var updated = IgnoreCommand.ApplyAdd(profile, tmp.Path, Home);

        await Assert.That(updated.ExcludedPaths).Contains(PathExclusion.Normalize(tmp.Path, Home));
    }

    [Test]
    public async Task ApplyAdd_does_not_duplicate_existing_entry() {
        using var tmp     = new TempDir();
        var       profile = new Profile { ExcludedPaths = [tmp.Path] };

        var updated = IgnoreCommand.ApplyAdd(profile, tmp.Path, Home);

        await Assert.That(updated.ExcludedPaths).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ApplyAdd_dedupes_equivalent_path_with_trailing_separator() {
        using var tmp     = new TempDir();
        var       profile = new Profile { ExcludedPaths = [tmp.Path] };

        var updated = IgnoreCommand.ApplyAdd(profile, tmp.Path + Path.DirectorySeparatorChar, Home);

        await Assert.That(updated.ExcludedPaths).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ApplyRemove_removes_matching_entry() {
        using var tmp     = new TempDir();
        var       profile = new Profile { ExcludedPaths = [tmp.Path] };

        var updated = IgnoreCommand.ApplyRemove(profile, tmp.Path, Home);

        await Assert.That(updated.ExcludedPaths).IsEmpty();
    }

    [Test]
    public async Task ApplyRemove_is_noop_when_path_absent() {
        using var tmp     = new TempDir();
        var       profile = new Profile { ExcludedPaths = [tmp.Path] };

        var updated = IgnoreCommand.ApplyRemove(profile, "/some/other/path", Home);

        await Assert.That(updated.ExcludedPaths).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ShouldExclude_excludes_when_path_excluded_even_if_repo_included() {
        // Regression: when a session matches BOTH an excluded repo and an excluded path,
        // opting-in to the repo must not bypass the path exclusion.
        var c             = MakeClassification(excludedRepoKeys: ["acme/secret"], excludedPathKey: "/home/alice/secret-dir");
        var includedRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acme/secret" };
        var includedPaths = new HashSet<string>(StringComparer.Ordinal);

        await Assert.That(ImportCommand.ShouldExclude(c, includedRepos, includedPaths)).IsTrue();
    }

    [Test]
    public async Task ShouldExclude_excludes_when_repo_excluded_even_if_path_included() {
        var c             = MakeClassification(excludedRepoKeys: ["acme/secret"], excludedPathKey: "/home/alice/secret-dir");
        var includedRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includedPaths = new HashSet<string>(StringComparer.Ordinal) { "/home/alice/secret-dir" };

        await Assert.That(ImportCommand.ShouldExclude(c, includedRepos, includedPaths)).IsTrue();
    }

    [Test]
    public async Task ShouldExclude_excludes_until_every_excluded_repo_is_opted_in() {
        var c             = MakeClassification(excludedRepoKeys: ["acme/public", "acme/secret"]);
        var includedRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acme/public" };
        var includedPaths = new HashSet<string>(StringComparer.Ordinal);

        await Assert.That(ImportCommand.ShouldExclude(c, includedRepos, includedPaths)).IsTrue();

        includedRepos.Add("acme/secret");

        await Assert.That(ImportCommand.ShouldExclude(c, includedRepos, includedPaths)).IsFalse();
    }

    [Test]
    public async Task ShouldExclude_includes_when_both_keys_opted_in() {
        var c             = MakeClassification(excludedRepoKeys: ["acme/secret"], excludedPathKey: "/home/alice/secret-dir");
        var includedRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acme/secret" };
        var includedPaths = new HashSet<string>(StringComparer.Ordinal) { "/home/alice/secret-dir" };

        await Assert.That(ImportCommand.ShouldExclude(c, includedRepos, includedPaths)).IsFalse();
    }

    [Test]
    public async Task ShouldExclude_returns_false_when_no_exclusion_keys() {
        var c = MakeClassification();

        await Assert.That(
                ImportCommand.ShouldExclude(
                    c,
                    new(StringComparer.OrdinalIgnoreCase),
                    new(StringComparer.Ordinal)
                )
            )
            .IsFalse();
    }

    [Test]
    public async Task ApplyAdd_skips_unnormalizable_existing_entries_and_still_adds() {
        // Hand-edited config can contain entries Path.GetFullPath rejects (null byte, etc.).
        // ApplyAdd must not crash iterating them; the user's new entry should still land.
        using var tmp     = new TempDir();
        var       profile = new Profile { ExcludedPaths = ["\0bad-entry"] };

        var updated = IgnoreCommand.ApplyAdd(profile, tmp.Path, Home);

        await Assert.That(updated.ExcludedPaths).Contains(PathExclusion.Normalize(tmp.Path, Home));
        await Assert.That(updated.ExcludedPaths).Contains("\0bad-entry");
    }

    [Test]
    public async Task ApplyRemove_skips_unnormalizable_existing_entries() {
        using var tmp     = new TempDir();
        var       profile = new Profile { ExcludedPaths = ["\0bad-entry", tmp.Path] };

        var updated = IgnoreCommand.ApplyRemove(profile, tmp.Path, Home);

        await Assert.That(updated.ExcludedPaths).Contains("\0bad-entry");
        await Assert.That(updated.ExcludedPaths).DoesNotContain(tmp.Path);
    }

    static ImportCommand.SessionClassification MakeClassification(
            string[]? excludedRepoKeys = null,
            string? excludedPathKey = null
        ) => new() {
        SessionId        = "abc",
        FilePath         = "/tmp/transcript.jsonl",
        EncodedCwd       = "-",
        Meta             = new SessionMetadata(),
        Status           = ImportCommand.ClassificationStatus.New,
        ExcludedRepoKeys = excludedRepoKeys ?? [],
        ExcludedPathKey  = excludedPathKey,
    };

    [Test]
    public async Task ApplyRemove_matches_with_trailing_separator() {
        using var tmp     = new TempDir();
        var       profile = new Profile { ExcludedPaths = [tmp.Path] };

        var updated = IgnoreCommand.ApplyRemove(profile, tmp.Path + Path.DirectorySeparatorChar, Home);

        await Assert.That(updated.ExcludedPaths).IsEmpty();
    }
}
