using System.Text;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>Setup's search-path refresh of a daemon run by a per-user Scheduled Task. cmd reads a running batch
/// file by offset, so the wrapper must never change under a running task.</summary>
public class WindowsScheduledTaskSetEnvironmentTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly List<string> _log = [];

    string Wrapper => WindowsTaskUnit.WrapperPath(Config.Root, "laptop");

    void Install(Dictionary<string, string> env) =>
        File.WriteAllText(Wrapper, WindowsTaskUnit.Wrapper(new ServiceSpec("laptop", @"C:\kcap\kcap-daemon.exe", @"C:\logs\daemon.log", env, [])));

    WindowsScheduledTaskServiceManager Manager(bool running) => new(
        Config.Root,
        writeUnit: (path, content, encoding) => {
            _log.Add("write");
            File.WriteAllText(path, content, encoding ?? Encoding.UTF8);
        },
        daemonPid: _ => null,
        schtasks: args => {
            _log.Add(args[0] == "/Query" && args.Contains("/XML") ? "/Query /XML" : args[0]);
            return args switch {
                ["/Query", .., "/XML"] => (0, WindowsTaskUnit.TaskXml(new ServiceSpec("laptop", "d", "l", new Dictionary<string, string>(), []), Wrapper, @"PC\u"), ""),
                ["/Query", ..]         => (0, running ? "Status: Running" : "Status: Ready", ""),
                _                      => (0, "", ""),
            };
        });

    [Test]
    public async Task A_running_task_is_ended_before_its_wrapper_changes_and_run_again() {
        Install(new() { ["PATH"] = @"C:\bin" });

        var outcome = Manager(running: true).SetEnvironment("laptop", "PATH", @"C:\bin;C:\npm", () => { _log.Add("stop"); return true; }, out var error);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Reloaded);
        await Assert.That(error).IsNull();
        await Assert.That(_log.SkipWhile(e => e != "stop")).IsEquivalentTo(["stop", "/End", "write", "/Run"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(WindowsTaskUnit.EnvFromWrapper(File.ReadAllText(Wrapper))["PATH"]).IsEqualTo(@"C:\bin;C:\npm");
    }

    [Test]
    public async Task A_daemon_that_refuses_the_stop_keeps_its_wrapper() {
        Install(new() { ["PATH"] = @"C:\bin" });
        var before = File.ReadAllText(Wrapper);

        var outcome = Manager(running: true).SetEnvironment("laptop", "PATH", @"C:\bin;C:\npm", () => false, out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Deferred);
        await Assert.That(File.ReadAllText(Wrapper)).IsEqualTo(before);
        await Assert.That(_log).DoesNotContain("/End");
        await Assert.That(_log).DoesNotContain("/Run");
    }

    /// <summary>A unit installed before the search path was captured on Windows has none; it gets one.</summary>
    [Test]
    public async Task A_stopped_task_takes_the_variable_at_its_next_start() {
        Install(new() { ["KCAP_PROFILE"] = "work" });

        var outcome = Manager(running: false).SetEnvironment("laptop", "PATH", @"C:\npm", () => throw new InvalidOperationException("must not stop"), out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.NotLoaded);
        await Assert.That(WindowsTaskUnit.EnvFromWrapper(File.ReadAllText(Wrapper))["PATH"]).IsEqualTo(@"C:\npm");
        await Assert.That(_log).DoesNotContain("/Run");
    }

    [Test]
    public async Task A_current_wrapper_is_left_alone() {
        Install(new() { ["PATH"] = @"C:\npm" });

        var outcome = Manager(running: true).SetEnvironment("laptop", "PATH", @"C:\npm", () => throw new InvalidOperationException("must not stop"), out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Current);
        await Assert.That(_log).DoesNotContain("write");
    }
}
