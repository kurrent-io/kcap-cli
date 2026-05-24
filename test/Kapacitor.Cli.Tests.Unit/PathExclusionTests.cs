using Kapacitor.Cli;

namespace Kapacitor.Cli.Tests.Unit;

public class PathExclusionTests {
    [Test]
    public async Task IsExcluded_returns_false_when_excludedPaths_is_null() {
        await Assert.That(PathExclusion.IsExcluded("/some/path", null)).IsFalse();
    }

    [Test]
    public async Task IsExcluded_returns_false_when_excludedPaths_is_empty() {
        await Assert.That(PathExclusion.IsExcluded("/some/path", [])).IsFalse();
    }

    [Test]
    public async Task IsExcluded_returns_false_when_cwd_is_null() {
        await Assert.That(PathExclusion.IsExcluded(null, ["/some/path"])).IsFalse();
    }

    [Test]
    public async Task IsExcluded_matches_exact_path() {
        using var tmp = TempDir.Create();
        var path = tmp.Path;

        await Assert.That(PathExclusion.IsExcluded(path, [path])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_matches_descendant() {
        using var tmp = TempDir.Create();
        var sub = Path.Combine(tmp.Path, "sub", "deeper");
        Directory.CreateDirectory(sub);

        await Assert.That(PathExclusion.IsExcluded(sub, [tmp.Path])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_does_not_match_sibling_with_shared_prefix() {
        // /tmp/foo vs /tmp/foobar — must NOT match
        using var tmp     = TempDir.Create();
        var       foo     = Path.Combine(tmp.Path, "foo");
        var       foobar  = Path.Combine(tmp.Path, "foobar");
        Directory.CreateDirectory(foo);
        Directory.CreateDirectory(foobar);

        await Assert.That(PathExclusion.IsExcluded(foobar, [foo])).IsFalse();
    }

    [Test]
    public async Task IsExcluded_ignores_trailing_separator_on_entry() {
        using var tmp = TempDir.Create();
        var sub = Path.Combine(tmp.Path, "child");
        Directory.CreateDirectory(sub);

        await Assert.That(PathExclusion.IsExcluded(sub, [tmp.Path + Path.DirectorySeparatorChar])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_matches_any_entry() {
        using var tmp = TempDir.Create();
        var       sub = Path.Combine(tmp.Path, "child");
        Directory.CreateDirectory(sub);

        await Assert.That(PathExclusion.IsExcluded(sub, ["/nonexistent/path", tmp.Path])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_resolves_symlinked_entry_against_real_cwd() {
        // User runs `kapacitor ignore /symlink-to-real` but session cwd reports
        // the resolved path. Both sides must normalize to the same target.
        using var real = TempDir.Create();
        using var link = TempSymlink.To(real.Path);

        // cwd uses the real path; entry uses the symlinked path.
        await Assert.That(PathExclusion.IsExcluded(real.Path, [link.Path])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_resolves_symlinked_cwd_against_real_entry() {
        // Reverse direction: entry stored as real path, cwd reported via symlink.
        using var real = TempDir.Create();
        using var link = TempSymlink.To(real.Path);

        await Assert.That(PathExclusion.IsExcluded(link.Path, [real.Path])).IsTrue();
    }

    [Test]
    public async Task Normalize_expands_tilde() {
        var home  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var normd = PathExclusion.Normalize("~");

        await Assert.That(normd).IsEqualTo(home.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Test]
    public async Task Normalize_expands_tilde_subpath() {
        var home  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(home, "stuff");
        var normd = PathExclusion.Normalize("~/stuff");

        await Assert.That(normd).IsEqualTo(expected);
    }

    [Test]
    public async Task Normalize_makes_relative_path_absolute() {
        var normd = PathExclusion.Normalize(".");

        await Assert.That(Path.IsPathRooted(normd)).IsTrue();
    }

    [Test]
    public async Task Normalize_strips_trailing_separator() {
        using var tmp = TempDir.Create();
        var withSlash = tmp.Path + Path.DirectorySeparatorChar;

        await Assert.That(PathExclusion.Normalize(withSlash))
            .DoesNotEndWith(Path.DirectorySeparatorChar.ToString());
    }
}

sealed class TempDir : IDisposable {
    public string Path { get; }

    TempDir(string path) => Path = path;

    public static TempDir Create() {
        var p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kap-pathex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);

        // On macOS, /var/folders is itself reached via a symlink. Resolve once
        // so tests that compare paths directly (without going through
        // PathExclusion.Normalize) operate on the canonical form.
        var resolved = new DirectoryInfo(p).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? p;

        return new(resolved);
    }

    public void Dispose() {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

sealed class TempSymlink : IDisposable {
    public string Path { get; }

    TempSymlink(string path) => Path = path;

    public static TempSymlink To(string target) {
        var p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kap-pathex-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(p, target);

        return new(p);
    }

    public void Dispose() {
        try { Directory.Delete(Path); } catch { /* best effort */ }
    }
}
