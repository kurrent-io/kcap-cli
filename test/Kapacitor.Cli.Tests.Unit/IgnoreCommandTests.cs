using Kapacitor.Cli.Commands;
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
    public async Task ApplyRemove_matches_with_trailing_separator() {
        using var tmp     = TempDir.Create();
        var       profile = new Profile { ExcludedPaths = [tmp.Path] };

        var updated = IgnoreCommand.ApplyRemove(profile, tmp.Path + Path.DirectorySeparatorChar);

        await Assert.That(updated.ExcludedPaths).IsEmpty();
    }
}
