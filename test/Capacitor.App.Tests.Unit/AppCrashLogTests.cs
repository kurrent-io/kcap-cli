using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class AppCrashLogTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static Exception Thrown(string message) {
        try {
            throw new InvalidOperationException(message);
        } catch (Exception ex) {
            return ex;
        }
    }

    AppCrashLog NewLog() => new(new ConfigRoot(Tmp.PathTo("config")), TimeProvider.System);

    [Test]
    public async Task Record_creates_the_config_dir_and_writes_source_type_message_and_stack() {
        var log = NewLog();

        log.Record("ui-thread", Thrown("boom-xyz"));

        var text = File.ReadAllText(log.Path);
        await Assert.That(text).Contains("source=ui-thread");
        await Assert.That(text).Contains("InvalidOperationException: boom-xyz");
        await Assert.That(text).Contains(nameof(Thrown));
    }

    [Test]
    public async Task The_same_exception_reported_twice_is_written_once() {
        var log = NewLog();
        var ex  = Thrown("once");

        log.Record("ui-thread", ex);
        log.Record("unhandled", ex);

        var text = File.ReadAllText(log.Path);
        await Assert.That(text).Contains("source=ui-thread");
        await Assert.That(text).DoesNotContain("source=unhandled");
    }

    [Test]
    public async Task Distinct_exceptions_are_appended() {
        var log = NewLog();

        log.Record("unhandled", Thrown("first"));
        log.Record("unhandled", Thrown("second"));

        var text = File.ReadAllText(log.Path);
        await Assert.That(text).Contains("first");
        await Assert.That(text).Contains("second");
    }

    [Test]
    public async Task A_log_past_the_cap_is_replaced_by_the_new_entry() {
        var log = NewLog();
        Tmp.CreateFile($"config/{AppCrashLog.FileName}", new string('x', 300 * 1024));

        log.Record("unhandled", Thrown("fresh"));

        var text = File.ReadAllText(log.Path);
        await Assert.That(text).DoesNotContain("xxxx");
        await Assert.That(text).Contains("fresh");
    }

    [Test]
    public async Task A_log_that_cannot_be_trimmed_still_takes_the_entry() {
        if (OperatingSystem.IsWindows()) return;

        var log    = NewLog();
        var config = Tmp.PathTo("config");
        Tmp.CreateFile($"config/{AppCrashLog.FileName}", new string('x', 300 * 1024));
        // A read-only directory refuses the delete but leaves the file itself writable.
        File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try {
            log.Record("unhandled", Thrown("kept"));
        } finally {
            File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Assert.That(File.ReadAllText(log.Path)).Contains("kept");
    }

    [Test]
    public async Task The_log_is_owner_only() {
        if (OperatingSystem.IsWindows()) return;

        var log = NewLog();
        log.Record("unhandled", Thrown("mode"));

        await Assert.That(File.GetUnixFileMode(log.Path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task An_existing_permissive_log_is_narrowed_to_owner_only() {
        if (OperatingSystem.IsWindows()) return;

        var log  = NewLog();
        var path = Tmp.CreateFile($"config/{AppCrashLog.FileName}", "earlier\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        log.Record("unhandled", Thrown("mode"));

        await Assert.That(File.GetUnixFileMode(log.Path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task A_linked_log_path_leaves_its_target_untouched() {
        if (OperatingSystem.IsWindows()) return;

        var log    = NewLog();
        var target = Tmp.CreateFile("elsewhere.txt", "target\n");
        Tmp.CreateDir("config");
        File.CreateSymbolicLink(log.Path, target);

        log.Record("unhandled", Thrown("redirected"));

        await Assert.That(File.ReadAllText(target)).IsEqualTo("target\n");
    }
}
