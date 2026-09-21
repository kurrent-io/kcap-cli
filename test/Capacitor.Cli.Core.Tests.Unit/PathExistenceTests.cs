namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>The one place existence is decided, and the one place absence is told apart from a
/// filesystem that would not answer.</summary>
public class PathExistenceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Presence_and_absence_are_both_established() {
        var dir  = Tmp.CreateDir("holder");
        var file = dir.CreateFile("ledger.json", "{}");

        await Assert.That(PathExistence.OfFile(file)).IsEqualTo(PathPresence.Present);
        await Assert.That(PathExistence.OfFile(dir.PathTo("absent.json"))).IsEqualTo(PathPresence.Missing);
        await Assert.That(PathExistence.OfFile(Tmp.PathTo("no-such-dir", "x.json")))
            .IsEqualTo(PathPresence.Missing);
        await Assert.That(PathExistence.OfDirectory(dir)).IsEqualTo(PathPresence.Present);
        await Assert.That(PathExistence.OfDirectory(Tmp.PathTo("absent"))).IsEqualTo(PathPresence.Missing);
        // A directory where a file is asked for, and the other way round, are each the entry being
        // there and not being what was asked for.
        await Assert.That(PathExistence.OfFile(dir)).IsEqualTo(PathPresence.Indeterminate);
    }

    /// <summary>A directory that cannot be searched answers "not found" for everything inside it.
    /// Reading that as absence is how a ledger is forgotten and a file is settled away.</summary>
    [Test]
    public async Task An_entry_that_cannot_be_answered_for_is_indeterminate() {
        Skip.When(OperatingSystem.IsWindows(), "file modes are the mechanism this inspects");

        var dir  = Tmp.CreateDir("holder");
        var file = dir.CreateFile("ledger.json", "{}");

        Mode(dir, UnixFileMode.UserRead);

        try {
            Skip.When(new FileInfo(file).Exists, "this user is not subject to the directory's mode");

            await Assert.That(PathExistence.OfFile(file)).IsEqualTo(PathPresence.Indeterminate);
        } finally {
            Mode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Assert.That(PathExistence.OfFile(file)).IsEqualTo(PathPresence.Present);
    }

    /// <summary>A directory that cannot be listed at all says nothing about what is under it
    /// either.</summary>
    [Test]
    public async Task An_unlistable_parent_answers_for_nothing_beneath_it() {
        Skip.When(OperatingSystem.IsWindows(), "file modes are the mechanism this inspects");

        var outer = Tmp.CreateDir("outer");
        var inner = outer.CreateDir("inner");

        Mode(outer, UnixFileMode.UserRead);

        try {
            Skip.When(Directory.Exists(inner), "this user is not subject to the directory's mode");

            await Assert.That(PathExistence.OfDirectory(inner)).IsEqualTo(PathPresence.Indeterminate);
        } finally {
            Mode(outer, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    static void Mode(string path, UnixFileMode mode) {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }
}
