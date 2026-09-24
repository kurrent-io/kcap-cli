using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

/// <summary>Tests for <see cref="RepoPathStore"/>. Each test gets its own root, so nothing here is
/// shared and nothing needs cleaning up between tests.</summary>
public class RepoPathStoreTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Lazy: injection happens after construction, so Config is not readable from an initializer.
    RepoPathStore Repos => field ??= new RepoPathStore(Config.Root, TimeProvider.System);

    string ReposJsonPath => Config.PathTo("repos.json");

    // ── LoadAsync ────────────────────────────────────────────────────────────

    [Test]
    public async Task Load_WhenFileDoesNotExist_ReturnsEmptyArray() {
        var entries = await Repos.LoadAsync();

        await Assert.That(entries).IsEmpty();
    }

    [Test]
    public async Task Load_WithMalformedJson_ReturnsEmptyArray() {
        await File.WriteAllTextAsync(ReposJsonPath, "this is not json at all {{{");

        var entries = await Repos.LoadAsync();

        await Assert.That(entries).IsEmpty();
    }

    [Test]
    public async Task Load_WithEmptyArray_ReturnsEmptyArray() {
        await File.WriteAllTextAsync(ReposJsonPath, "[]");

        var entries = await Repos.LoadAsync();

        await Assert.That(entries).IsEmpty();
    }

    // ── AddAsync ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Add_WhenFileDoesNotExist_CreatesFileWithEntry() {
        var path = "/tmp/my-project";

        await Repos.AddAsync(path);

        await Assert.That(File.Exists(ReposJsonPath)).IsTrue();
        var entries = await Repos.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Add_NewPath_AppearsInLoad() {
        var path = "/tmp/my-project";

        await Repos.AddAsync(path);

        var entries = await Repos.LoadAsync();
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        await Assert.That(entries.Any(e => e.Path == normalized)).IsTrue();
    }

    [Test]
    public async Task Add_SamePathTwice_DoesNotCreateDuplicate() {
        var path = "/tmp/my-project";

        await Repos.AddAsync(path);
        await Repos.AddAsync(path);

        var entries = await Repos.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Add_SamePathTwice_UpdatesLastUsed() {
        var path = "/tmp/my-project";

        await Repos.AddAsync(path);
        var firstEntries = await Repos.LoadAsync();
        var firstLastUsed = firstEntries[0].LastUsed;

        // Small delay to ensure DateTimeOffset.UtcNow advances
        await Task.Delay(10);

        await Repos.AddAsync(path);
        var secondEntries = await Repos.LoadAsync();
        var secondLastUsed = secondEntries[0].LastUsed;

        await Assert.That(secondLastUsed).IsGreaterThan(firstLastUsed);
    }

    [Test]
    public async Task Add_MultiplePaths_AllPresentInLoad() {
        await Repos.AddAsync("/tmp/project-a");
        await Repos.AddAsync("/tmp/project-b");
        await Repos.AddAsync("/tmp/project-c");

        var entries = await Repos.LoadAsync();

        await Assert.That(entries.Length).IsEqualTo(3);
    }

    // ── Path normalization ────────────────────────────────────────────────────

    [Test]
    public async Task Add_PathWithTrailingSeparator_IsNormalized() {
        var pathWithSep    = "/tmp/my-project" + Path.DirectorySeparatorChar;
        var pathWithoutSep = "/tmp/my-project";

        await Repos.AddAsync(pathWithSep);

        var entries = await Repos.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
        await Assert.That(entries[0].Path).IsEqualTo(Path.GetFullPath(pathWithoutSep));
    }

    [Test]
    public async Task Add_SamePathWithAndWithoutTrailingSeparator_TreatedAsSamePath() {
        var pathWithSep    = "/tmp/my-project" + Path.DirectorySeparatorChar;
        var pathWithoutSep = "/tmp/my-project";

        await Repos.AddAsync(pathWithSep);
        await Repos.AddAsync(pathWithoutSep);

        var entries = await Repos.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
    }

    // ── RemoveAsync ───────────────────────────────────────────────────────────

    [Test]
    public async Task Remove_ExistingPath_ReturnsTrue() {
        var path = "/tmp/my-project";
        await Repos.AddAsync(path);

        var result = await Repos.RemoveAsync(path);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task Remove_ExistingPath_PathNoLongerInLoad() {
        var path = "/tmp/my-project";
        await Repos.AddAsync(path);

        await Repos.RemoveAsync(path);

        var entries = await Repos.LoadAsync();
        await Assert.That(entries).IsEmpty();
    }

    [Test]
    public async Task Remove_NonExistentPath_ReturnsFalse() {
        var result = await Repos.RemoveAsync("/tmp/does-not-exist");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task Remove_OneOfMultiplePaths_OthersRemain() {
        await Repos.AddAsync("/tmp/project-a");
        await Repos.AddAsync("/tmp/project-b");
        await Repos.AddAsync("/tmp/project-c");

        await Repos.RemoveAsync("/tmp/project-b");

        var entries = await Repos.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(2);
        await Assert.That(entries.Any(e => e.Path.EndsWith("project-b", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Remove_WhenFileDoesNotExist_ReturnsFalse() {
        var result = await Repos.RemoveAsync("/tmp/nonexistent");

        await Assert.That(result).IsFalse();
    }

    // ── GetSortedPathsAsync ───────────────────────────────────────────────────

    [Test]
    public async Task GetSortedPaths_WhenEmpty_ReturnsEmptyArray() {
        var paths = await Repos.GetSortedPathsAsync();

        await Assert.That(paths).IsEmpty();
    }

    [Test]
    public async Task GetSortedPaths_ReturnsMostRecentlyUsedFirst() {
        await Repos.AddAsync("/tmp/project-old");
        await Task.Delay(10);
        await Repos.AddAsync("/tmp/project-new");

        var paths = await Repos.GetSortedPathsAsync();

        await Assert.That(paths.Length).IsEqualTo(2);
        await Assert.That(paths[0]).IsEqualTo(Path.GetFullPath("/tmp/project-new"));
        await Assert.That(paths[1]).IsEqualTo(Path.GetFullPath("/tmp/project-old"));
    }

    [Test]
    public async Task GetSortedPaths_AfterReAdding_MovesPathToFront() {
        await Repos.AddAsync("/tmp/project-a");
        await Task.Delay(10);
        await Repos.AddAsync("/tmp/project-b");
        await Task.Delay(10);

        // Re-add project-a, which should update its LastUsed and move it to front
        await Repos.AddAsync("/tmp/project-a");

        var paths = await Repos.GetSortedPathsAsync();

        await Assert.That(paths[0]).IsEqualTo(Path.GetFullPath("/tmp/project-a"));
        await Assert.That(paths[1]).IsEqualTo(Path.GetFullPath("/tmp/project-b"));
    }

    [Test]
    public async Task GetSortedPaths_ReturnOnlyPaths_NotFullEntries() {
        await Repos.AddAsync("/tmp/project-x");

        var paths = await Repos.GetSortedPathsAsync();

        // Verify it's string[], not RepoEntry[]
        await Assert.That(paths.Length).IsEqualTo(1);
        await Assert.That(paths[0]).IsEqualTo(Path.GetFullPath("/tmp/project-x"));
    }

    // ── Worktree resolution (GH #655) ─────────────────────────────────────────

    /// Agent registrations persist the launch path, and review flows launch into the requester's
    /// worktree — the store, not the caller, is where that collapses to the main repository, so
    /// every consumer (server launch dialog, app menu, `kcap repos list`) inherits it.
    [Test]
    public async Task Add_resolves_a_linked_worktree_to_its_main_repository() {
        using var tmp = new TempDir();
        var main = tmp.CreateDir("main");
        tmp.CreateDir("main", ".git", "worktrees", "wt1");
        var wt = tmp.CreateDir("wt");
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "wt1")}\n");

        await Repos.AddAsync(main);
        await Repos.AddAsync(wt);

        var entries = await Repos.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
        await Assert.That(entries[0].Path).IsEqualTo(Path.GetFullPath(main));
    }

    /// Historical pollution: entries written before the resolution existed, whose worktree
    /// directories are long gone. Read-side resolution collapses them without a migration,
    /// keeping the newest last_used per surviving repository.
    [Test]
    public async Task Load_collapses_dead_worktree_entries_into_their_repository() {
        var older = DateTimeOffset.UtcNow.AddDays(-2);
        var newer = DateTimeOffset.UtcNow.AddDays(-1);
        var polluted = new RepoEntry[] {
            new() { Path = "/gone/repo", LastUsed = older },
            new() { Path = "/gone/repo/.claude/worktrees/leaf", LastUsed = newer },
            new() { Path = "/gone/other", LastUsed = older },
        };
        await File.WriteAllTextAsync(ReposJsonPath, System.Text.Json.JsonSerializer.Serialize(polluted));

        var entries = await Repos.LoadAsync();

        await Assert.That(entries.Length).IsEqualTo(2);
        // GetFullPath, like every assertion in this class: on Windows a rootless "/gone/repo"
        // normalizes to "<drive>:\gone\repo".
        var repo = entries.Single(e => e.Path == Path.GetFullPath("/gone/repo"));
        await Assert.That(repo.LastUsed).IsEqualTo(newer);
    }

    // ── Fingerprint ──────────────────────────────────────────────────────────

    [Test]
    public async Task Fingerprint_WhenFileDoesNotExist_IsNull() {
        await Assert.That(Repos.Fingerprint()).IsNull();
    }

    [Test]
    public async Task Fingerprint_WithoutAWrite_IsStable() {
        await Repos.AddAsync("/tmp/project-a");

        await Assert.That(Repos.Fingerprint()).IsEqualTo(Repos.Fingerprint());
    }

    [Test]
    public async Task Fingerprint_ChangesWhenAPathIsAdded() {
        await Repos.AddAsync("/tmp/project-a");
        var before = Repos.Fingerprint();

        await Repos.AddAsync("/tmp/project-b");

        await Assert.That(Repos.Fingerprint()).IsNotEqualTo(before);
    }

    [Test]
    public async Task Fingerprint_ChangesWhenAPathIsRemoved() {
        await Repos.AddAsync("/tmp/project-a");
        await Repos.AddAsync("/tmp/project-b");
        var before = Repos.Fingerprint();

        await Repos.RemoveAsync("/tmp/project-b");

        await Assert.That(Repos.Fingerprint()).IsNotEqualTo(before);
    }

    // Re-adding a known path rewrites the file at the same length, and on a filesystem with coarse
    // timestamps the second write can carry the first one's mtime.
    [Test]
    public async Task Fingerprint_TellsApartSameLengthWritesWithTheSameTimestamp() {
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await File.WriteAllTextAsync(ReposJsonPath, "[a]");
        File.SetLastWriteTimeUtc(ReposJsonPath, stamp);
        var before = Repos.Fingerprint();

        await File.WriteAllTextAsync(ReposJsonPath, "[b]");
        File.SetLastWriteTimeUtc(ReposJsonPath, stamp);

        await Assert.That(Repos.Fingerprint()).IsNotEqualTo(before);
    }

    [Test]
    public async Task Fingerprint_IgnoresATouchThatLeavesTheContentAlone() {
        await Repos.AddAsync("/tmp/project-a");
        var before = Repos.Fingerprint();

        File.SetLastWriteTimeUtc(ReposJsonPath, DateTime.UtcNow.AddMinutes(1));

        await Assert.That(Repos.Fingerprint()).IsEqualTo(before);
    }

    // ── an unreadable repos.json is never overwritten ────────────────────────

    string BackupPath => Config.PathTo("repos.json.bak");

    static string[] Leaves(string[] paths) => [.. paths.Select(p => Path.GetFileName(p))];

    // What NTFS can leave after a power loss between an unflushed write and the rename that follows it.
    async Task WriteZeroFilledStoreAsync() => await File.WriteAllBytesAsync(ReposJsonPath, new byte[64]);

    [Test]
    public async Task TryLoad_is_null_for_a_zero_filled_file_and_empty_only_when_absent() {
        await Assert.That(await Repos.TryLoadAsync()).IsEmpty();

        await WriteZeroFilledStoreAsync();

        await Assert.That(await Repos.TryLoadAsync()).IsNull();
        await Assert.That(await Repos.TryGetSortedPathsAsync()).IsNull();
        await Assert.That(await Repos.GetSortedPathsAsync()).IsEmpty();
    }

    /// The loss the daemon reported as "repositories gone after a restart": the next launch's AddAsync
    /// read the unreadable file as empty and saved that, plus one entry, over it.
    [Test]
    public async Task Add_refuses_to_overwrite_an_unreadable_file_without_a_backup() {
        await WriteZeroFilledStoreAsync();

        await Assert.That(async () => await Repos.AddAsync("/tmp/new")).Throws<IOException>();

        await Assert.That(await File.ReadAllBytesAsync(ReposJsonPath)).IsEquivalentTo(new byte[64]);
    }

    [Test]
    public async Task Each_save_keeps_the_previous_list_as_a_backup() {
        await Repos.AddAsync("/tmp/project-a");
        await Repos.AddAsync("/tmp/project-b");

        var backup = await File.ReadAllTextAsync(BackupPath);

        await Assert.That(backup).Contains("project-a");
        await Assert.That(backup).DoesNotContain("project-b");
    }

    [Test]
    public async Task An_unreadable_file_falls_back_to_its_backup_and_the_next_write_keeps_those_repos() {
        await Repos.AddAsync("/tmp/project-a");
        await Repos.AddAsync("/tmp/project-b");
        await WriteZeroFilledStoreAsync();

        var recovered = await Repos.GetSortedPathsAsync();
        await Repos.AddAsync("/tmp/project-c");

        await Assert.That(Leaves(recovered)).IsEquivalentTo(["project-a"]);
        await Assert.That(Leaves(await Repos.GetSortedPathsAsync()))
            .IsEquivalentTo(["project-a", "project-c"], TUnit.Assertions.Enums.CollectionOrdering.Any);
    }

    /// On Windows a handle opened without delete sharing blocks the rename that saves the list. A reader
    /// that lets go shortly is waited out rather than turned into a lost save.
    [Test]
    public async Task A_briefly_held_file_is_saved_once_the_holder_lets_go() {
        await Repos.AddAsync("/tmp/project-a");
        var holder = new FileStream(ReposJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => {
            await Task.Delay(150);
            await holder.DisposeAsync();
        });

        await Repos.AddAsync("/tmp/project-b");
        await release;

        await Assert.That(Leaves(await Repos.GetSortedPathsAsync()))
            .IsEquivalentTo(["project-a", "project-b"], TUnit.Assertions.Enums.CollectionOrdering.Any);
    }

    [Test]
    public async Task A_root_keeps_its_separator_and_other_paths_lose_theirs() {
        var root = Path.GetPathRoot(Path.GetFullPath("/"))!;

        await Assert.That(RepoPathStore.NormalizePath(root)).IsEqualTo(root);
        await Assert.That(RepoPathStore.NormalizePath(Path.Combine(root, "src") + Path.DirectorySeparatorChar))
            .IsEqualTo(Path.Combine(root, "src"));
    }

    [Test]
    public async Task Reading_does_not_block_a_concurrent_save() {
        await Repos.AddAsync("/tmp/project-a");
        await using var reader = new FileStream(ReposJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        await Repos.AddAsync("/tmp/project-b");

        await Assert.That((await Repos.GetSortedPathsAsync()).Length).IsEqualTo(2);
    }
}
