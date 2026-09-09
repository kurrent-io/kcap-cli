using System.Diagnostics;
using System.Globalization;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="ProcessTree"/>: the daemon's tree kill. It has to reach grandchildren (a hosted agent's
/// bridge children, a shell's jobs) through a parent that is killed first, and it does so with SIGKILL
/// alone. The tree here is <c>sh -c 'sleep &amp; sleep &amp; wait'</c>; the grandchildren are located
/// through <c>ps</c> so the walk under test is checked against an independent view.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class ProcessTreeTests {
    [Test]
    public async Task Descendants_include_the_grandchildren() {
        if (OperatingSystem.IsWindows()) return;

        using var root = StartTree();
        var grandchildren = await WaitForChildrenAsync(root.Id, 2);

        var found = ProcessTree.Descendants(root.Id);

        await Assert.That(found).Contains(grandchildren[0]);
        await Assert.That(found).Contains(grandchildren[1]);
        await Assert.That(found).DoesNotContain(root.Id);

        ProcessTree.Kill(root);
        root.WaitForExit(5000);
    }

    [Test]
    public async Task Kill_ends_the_whole_tree() {
        if (OperatingSystem.IsWindows()) return;

        using var root = StartTree();
        var grandchildren = await WaitForChildrenAsync(root.Id, 2);

        ProcessTree.Kill(root);

        await Assert.That(root.WaitForExit(5000)).IsTrue();
        await Assert.That(await WaitUntilGoneAsync(grandchildren)).IsTrue();
    }

    [Test]
    public async Task Kill_of_an_exited_process_does_nothing() {
        using var done = Process.Start(new ProcessStartInfo("sh", ["-c", "exit 0"]) { UseShellExecute = false })!;
        done.WaitForExit();

        ProcessTree.Kill(done);

        await Assert.That(done.HasExited).IsTrue();
    }

    static Process StartTree() =>
        Process.Start(new ProcessStartInfo("sh", ["-c", "sleep 300 & sleep 300 & wait"]) { UseShellExecute = false })!;

    /// <summary>Children of <paramref name="parent"/> as <c>ps</c> sees them, once at least
    /// <paramref name="count"/> exist.</summary>
    static async Task<List<int>> WaitForChildrenAsync(int parent, int count) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (true) {
            var children = ChildrenViaPs(parent);
            if (children.Count >= count) return children;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"pid {parent} never showed {count} children; saw {children.Count}");
            await Task.Delay(50);
        }
    }

    static List<int> ChildrenViaPs(int parent) {
        using var ps = Process.Start(new ProcessStartInfo("ps", ["-axo", "pid=,ppid="]) {
            UseShellExecute = false, RedirectStandardOutput = true,
        })!;
        var output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();

        var children = new List<int>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && int.Parse(fields[1], CultureInfo.InvariantCulture) == parent)
                children.Add(int.Parse(fields[0], CultureInfo.InvariantCulture));
        }

        return children;
    }

    static async Task<bool> WaitUntilGoneAsync(IEnumerable<int> pids) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (pids.Any(ProcessIdentity.IsAlive)) {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(50);
        }

        return true;
    }
}
