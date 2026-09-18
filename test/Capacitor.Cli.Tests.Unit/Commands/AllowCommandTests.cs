using Capacitor.Cli.Core;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap allow</c> edits <c>allowed_paths</c> through the same editor <c>kcap ignore</c> uses,
/// so these cover the wiring — that it writes the allow list and leaves the deny list alone.
/// </summary>
public class AllowCommandTests {
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task ApplyAdd_appends_normalized_path_to_allowed_paths() {
        using var tmp     = new TempDir();
        var       profile = new Profile();

        var updated = AllowCommand.ApplyAdd(profile, tmp.Path, Home);

        await Assert.That(updated.AllowedPaths).Contains(PathExclusion.Normalize(tmp.Path, Home));
    }

    [Test]
    public async Task ApplyAdd_does_not_touch_excluded_paths() {
        using var tmp     = new TempDir();
        var       profile = new Profile { ExcludedPaths = ["/already/ignored"] };

        var updated = AllowCommand.ApplyAdd(profile, tmp.Path, Home);

        await Assert.That(updated.ExcludedPaths).IsEquivalentTo(["/already/ignored"]);
    }

    [Test]
    public async Task ApplyAdd_dedupes_equivalent_path_with_trailing_separator() {
        using var tmp     = new TempDir();
        var       profile = new Profile { AllowedPaths = [tmp.Path] };

        var updated = AllowCommand.ApplyAdd(profile, tmp.Path + Path.DirectorySeparatorChar, Home);

        await Assert.That(updated.AllowedPaths).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ApplyRemove_removes_matching_entry() {
        using var tmp     = new TempDir();
        var       profile = new Profile { AllowedPaths = [tmp.Path] };

        var updated = AllowCommand.ApplyRemove(profile, tmp.Path, Home);

        await Assert.That(updated.AllowedPaths).IsEmpty();
    }

    [Test]
    public async Task ApplyRemove_is_noop_when_path_absent() {
        using var tmp     = new TempDir();
        var       profile = new Profile { AllowedPaths = [tmp.Path] };

        var updated = AllowCommand.ApplyRemove(profile, "/some/other/path", Home);

        await Assert.That(updated.AllowedPaths).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ApplyAdd_treats_a_null_stored_list_as_empty() {
        // JSON source-gen leaves an absent key null despite the `= []` initializer.
        using var tmp     = new TempDir();
        var       profile = new Profile { AllowedPaths = null! };

        var updated = AllowCommand.ApplyAdd(profile, tmp.Path, Home);

        await Assert.That(updated.AllowedPaths).Count().IsEqualTo(1);
    }
}
