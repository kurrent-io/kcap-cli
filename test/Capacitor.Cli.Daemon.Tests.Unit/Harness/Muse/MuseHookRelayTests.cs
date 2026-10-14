using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Capacitor.Cli.Daemon.Harness.Muse;
using Capacitor.Cli.Daemon.Pty.Unix;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

[ParallelLimiter<SubprocessLimit>]
public class MuseHookRelayTests {
    /// <summary>A hook that exits while a detached child still holds its output pipes must not hold the
    /// relay, and with it the launch, until that child exits. The stub's child outlives the exit ceiling,
    /// so an unbounded drain would take longer than the assertion allows.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_hook_whose_child_keeps_its_output_open_does_not_hold_the_relay() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub hook below is a POSIX shell script.");

        using var tmp = new TempDir();
        var pidFile = tmp.PathTo("child.pid");
        var stub    = tmp.CreateExecutable("kcap", $"#!/bin/sh\ncat > /dev/null\nsleep 25 &\necho $! > '{pidFile}'\nexit 0\n");

        // The child is reparented once the stub exits, so its pid is not this process's to signal
        // unless it still carries the identity captured while it was provably alive.
        var childPid      = 0;
        var childIdentity = "";

        try {
            var watch = Stopwatch.StartNew();

            await MuseHookRelay.RelayAsync(stub, new JsonObject { ["hook_event_name"] = "SessionStart" },
                "01a13696-9235-7b32-a9e5-3d3e067625e2", "agent-1", null, "daemon-1", "epoch-1",
                NullLogger.Instance, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

            watch.Stop();

            await Assert.That(File.Exists(pidFile)).IsTrue();
            childPid      = int.Parse(File.ReadAllText(pidFile).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            childIdentity = PidIdentity.Capture(childPid);

            await Assert.That(watch.Elapsed).IsLessThan(MuseHookRelay.DrainCeiling + TimeSpan.FromSeconds(5));
        } finally {
            if (childPid > 0 && !PidIdentity.IsGone(childPid, childIdentity))
                UnixPtyInterop.kill(childPid, UnixPtyInterop.SIGKILL);
        }
    }
}
