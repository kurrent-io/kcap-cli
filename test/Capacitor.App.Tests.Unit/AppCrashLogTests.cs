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
        Directory.CreateDirectory(Tmp.PathTo("config"));
        File.WriteAllText(log.Path, new string('x', 300 * 1024));

        log.Record("unhandled", Thrown("fresh"));

        var text = File.ReadAllText(log.Path);
        await Assert.That(text).DoesNotContain("xxxx");
        await Assert.That(text).Contains("fresh");
    }

    [Test]
    public async Task The_log_is_owner_only() {
        if (OperatingSystem.IsWindows()) return;

        var log = NewLog();
        log.Record("unhandled", Thrown("mode"));

        await Assert.That(File.GetUnixFileMode(log.Path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
