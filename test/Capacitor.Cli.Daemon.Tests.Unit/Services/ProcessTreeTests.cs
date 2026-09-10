using System.Diagnostics;
using System.Globalization;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="ProcessTree"/>: the daemon's tree kill has to reach every level below a parent that is
/// killed after its children, with SIGKILL alone. The trees are shells with backgrounded
/// <c>sleep</c>s; their descendants are located through <c>ps</c>, so the walk under test is checked
/// against an independent view, and each is pinned by <see cref="PidIdentity"/> so a recycled pid can
/// fake neither survival nor death.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class ProcessTreeTests {
    [Test]
    public async Task Kill_ends_children_and_grandchildren() {
        if (OperatingSystem.IsWindows()) return;

        using var root = StartTree("sleep 300 & sleep 300 & wait");
        var grandchildren = await PinChildrenAsync(root.Id, 2);

        ProcessTree.Kill(root);

        await Assert.That(root.WaitForExit(5000)).IsTrue();
        foreach (var (pid, identity) in grandchildren)
            await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Kill_reaches_the_third_level() {
        if (OperatingSystem.IsWindows()) return;

        using var root = StartTree("sh -c 'sleep 300 & sleep 300 & wait' & wait");
        var (shell, shellIdentity) = (await PinChildrenAsync(root.Id, 1)).Single();
        var leaves = await PinChildrenAsync(shell, 2);

        ProcessTree.Kill(root);

        await Assert.That(root.WaitForExit(5000)).IsTrue();
        await PidIdentity.WaitUntilGoneAsync(shell, shellIdentity, TimeSpan.FromSeconds(5));
        foreach (var (pid, identity) in leaves)
            await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Kill_of_an_exited_process_does_nothing() {
        using var done = Process.Start(new ProcessStartInfo("sh", ["-c", "exit 0"]) { UseShellExecute = false })!;
        done.WaitForExit();

        ProcessTree.Kill(done);

        await Assert.That(done.HasExited).IsTrue();
    }

    static Process StartTree(string script) =>
        Process.Start(new ProcessStartInfo("sh", ["-c", script]) { UseShellExecute = false })!;

    /// <summary>The children of <paramref name="parent"/> as <c>ps</c> sees them, once at least
    /// <paramref name="count"/> exist, each pinned to its incarnation while it is known alive.</summary>
    static async Task<List<(int Pid, string Identity)>> PinChildrenAsync(int parent, int count) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (true) {
            var children = ChildrenViaPs(parent);
            if (children.Count >= count) return children.Select(pid => (pid, PidIdentity.Capture(pid))).ToList();
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
}
