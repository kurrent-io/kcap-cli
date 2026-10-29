using Capacitor.App.Services;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class InstalledAppProcessesTests {
    [Test]
    public async Task Unknown_process_identity_does_not_send_a_quit_request() {
        var calls = 0;
        var result = await InstalledAppProcesses.QuitAsync("/Applications/kcap.app", TimeProvider.System, CancellationToken.None,
            _ => null, _ => { calls++; return true; });
        await Assert.That(result).IsFalse();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task Only_verified_installed_processes_receive_quit_and_exit_is_required() {
        var running = new List<int> { 123, 456 };
        var requested = new List<int>();
        var result = await InstalledAppProcesses.QuitAsync("/Applications/kcap.app", TimeProvider.System, CancellationToken.None,
            _ => running.ToArray(), pid => { requested.Add(pid); running.Remove(pid); return true; });
        await Assert.That(result).IsTrue();
        await Assert.That(requested).IsEquivalentTo([123, 456], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_refused_quit_blocks_installation_without_targeting_another_app() {
        var requests = new List<int>();
        var result = await InstalledAppProcesses.QuitAsync("/Applications/kcap.app", TimeProvider.System, CancellationToken.None,
            _ => [123], pid => { requests.Add(pid); return false; });
        await Assert.That(result).IsFalse();
        await Assert.That(requests).IsEquivalentTo([123], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Accepting_quit_does_not_count_as_exit_and_the_wait_is_bounded() {
        var time = new FakeTimeProvider();
        var pending = InstalledAppProcesses.QuitAsync("/Applications/kcap.app", time, CancellationToken.None,
            _ => [123], _ => true);
        await Assert.That(pending.IsCompleted).IsFalse();
        time.Advance(TimeSpan.FromSeconds(31));
        await Assert.That(await pending).IsFalse();
    }

    [Test]
    public async Task The_installer_process_is_never_asked_to_quit() {
        var calls = 0;
        var result = await InstalledAppProcesses.QuitAsync("/Applications/kcap.app", TimeProvider.System, CancellationToken.None,
            _ => [Environment.ProcessId], _ => { calls++; return true; });
        await Assert.That(result).IsFalse();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task A_refused_quit_does_not_keep_the_others_from_quitting() {
        var running = new List<int> { 123, 456 };
        var result = await InstalledAppProcesses.QuitEarlierAsync(DateTime.UtcNow, TimeProvider.System, CancellationToken.None,
            _ => running.ToArray(), pid => { if (pid == 123) return false; running.Remove(pid); return true; });
        await Assert.That(result).IsEqualTo((1, true));
    }

    [Test]
    public async Task Waiting_for_an_earlier_copy_to_exit_is_bounded() {
        var time = new FakeTimeProvider();
        var pending = InstalledAppProcesses.QuitEarlierAsync(DateTime.UtcNow, time, CancellationToken.None, _ => [123], _ => true);
        await Assert.That(pending.IsCompleted).IsFalse();
        time.Advance(TimeSpan.FromSeconds(31));
        await Assert.That(await pending).IsEqualTo((1, false));
    }

    [Test]
    public async Task This_process_is_never_asked_to_quit() {
        var calls = 0;
        var result = await InstalledAppProcesses.QuitEarlierAsync(DateTime.UtcNow, TimeProvider.System, CancellationToken.None,
            _ => [Environment.ProcessId], _ => { calls++; return true; });
        await Assert.That(result).IsEqualTo((0, true));
        await Assert.That(calls).IsEqualTo(0);
    }

    /// A matching executable name is not enough: only a process registered under a kcap bundle identifier is a copy of kcap.
    [Test]
    public async Task Only_an_earlier_process_registered_as_kcap_counts_as_an_earlier_copy() {
        var now = DateTime.UtcNow;
        var candidates = new[] {
            (Pid: 101, Started: now.AddMinutes(-5)), (Pid: 102, Started: now.AddMinutes(-5)), (Pid: 103, Started: now.AddMinutes(-5)),
            (Pid: 104, Started: now.AddMinutes(-5)), (Pid: 105, Started: now.AddMinutes(1)),
        };
        var identifiers = new Dictionary<int, string?> {
            [101] = "io.kurrent.kcap", [102] = "io.kurrent.capacitor", [103] = "com.example.other", [104] = null, [105] = "io.kurrent.kcap",
        };

        var earlier = InstalledAppProcesses.FindEarlier(now, candidates, pid => identifiers[pid]);

        await Assert.That(earlier).IsEquivalentTo([101, 102], CollectionOrdering.Matching);
    }
}
