using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Integration;

[ExcludeOn(OS.Windows)]
public class AgentStopTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string AgentId = "0123456789abcdef0123456789abcdef";
    const string Table = "plain-1\tRunning\t/repo\tagent\t\t\n"
                       + "plain-2\tRunning\t/repo\tagent\t\t\n"
                       + "review-1\tRunning\t/repo\treview-flow\tflow-1\treviewer";

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Stop_all_sends_only_the_ids_from_the_confirmation(bool force, bool yes) {
        string[] args = ["stop", "--all", .. force ? new[] { "--force" } : [], .. yes ? new[] { "-y" } : []];
        var (stdout, _, exitCode, stops) = await RunCliAsync(args, Table);

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stops.Select(s => s.Id)).IsEquivalentTo(
            force ? new[] { "plain-1", "plain-2", "review-1" } : ["plain-1", "plain-2"]);
        await Assert.That(stops.All(s => s.Force == force)).IsTrue();
        await Assert.That(stdout).Contains(force ? "Including 1 review" : "Skipping 1 review");
        if (!yes) await Assert.That(stdout).Contains("[y/N]");
    }

    [Test]
    public async Task Stop_all_attempts_every_confirmed_agent_even_if_one_fails() {
        var (_, _, exitCode, stops) = await RunCliAsync(["stop", "--all", "-y"], Table,
            id => id == "plain-1" ? LocalFrame.Error("stop failed") : LocalFrame.StopAck($"{id}\tstopped"));

        await Assert.That(stops.Select(s => s.Id)).IsEquivalentTo(new[] { "plain-1", "plain-2" });
        await Assert.That(exitCode).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Stop_all_accepts_a_confirmed_agent_that_finished_before_dispatch(bool force, bool yes) {
        string[] args = ["stop", "--all", .. force ? new[] { "--force" } : [], .. yes ? new[] { "-y" } : []];
        var (stdout, stderr, exitCode, stops) = await RunCliAsync(args, Table,
            id => LocalFrame.StopAck($"{id}\t{(id == "plain-1" ? "missing" : "stopped")}"));

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stderr).DoesNotContain("Failed to stop");
        await Assert.That(stderr).DoesNotContain("no such agent");
        await Assert.That(stdout).Contains("Already stopped plain-1.");
        await Assert.That(stops.Select(s => s.Id)).IsEquivalentTo(
            force ? new[] { "plain-1", "plain-2", "review-1" } : ["plain-1", "plain-2"]);
    }

    [Test]
    [Arguments("plain-1\tfailed")]
    [Arguments("other-agent\tmissing")]
    [Arguments("plain-1\tmissing\textra")]
    public async Task Stop_all_rejects_unconfirmed_or_mismatched_stop_results(string ack) {
        var (_, _, exitCode, _) = await RunCliAsync(["stop", "--all", "-y"], Table,
            id => id == "plain-1" ? LocalFrame.StopAck(ack) : LocalFrame.StopAck($"{id}\tstopped"));

        await Assert.That(exitCode).IsEqualTo(1);
    }

    [Test]
    public async Task Stop_all_does_not_ignore_an_older_daemons_ambiguous_missing_error() {
        var (_, stderr, exitCode, _) = await RunCliAsync(["stop", "--all", "-y"], Table,
            id => LocalFrame.Error($"no such agent {id}"));

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).Contains("no such agent");
    }

    [Test]
    public async Task A_single_missing_agent_still_fails() {
        var (_, stderr, exitCode, _) = await RunCliAsync(["stop", AgentId], "",
            id => LocalFrame.StopAck($"{id}\tmissing"));

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).Contains($"no such agent {AgentId}");
    }

    [Test]
    public async Task Stop_all_dispatches_the_confirmed_stops_concurrently() {
        var (_, _, exitCode, stops) = await RunCliAsync(["stop", "--all", "-y"], Table, stopRepliesAfter: 2);

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stops.Select(s => s.Id)).IsEquivalentTo(new[] { "plain-1", "plain-2" });
    }

    [Test]
    public async Task Cancelling_the_confirmation_sends_no_stop_requests() {
        var (stdout, _, exitCode, stops) = await RunCliAsync(["stop", "--all"], Table, confirmation: "n");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stops).IsEmpty();
        await Assert.That(stdout).Contains("Cancelled");
    }

    [Test]
    [Arguments("")]
    [Arguments("review-1\tRunning\t/repo\treview-flow\tflow-1\treviewer")]
    public async Task An_empty_stop_selection_sends_no_stop_requests(string table) {
        var (_, _, exitCode, stops) = await RunCliAsync(["stop", "--all", "-y"], table);

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stops).IsEmpty();
    }

    [Test]
    [Arguments("a\tstopped", 0)]
    [Arguments("a\tstopped\nb\tskipped", 0)]
    [Arguments("a\tstopped\nb\tfailed", 1)]
    [Arguments("a\tstopped\nb\tskipped\nc\tfailed", 1)]
    [Arguments("", 0)]
    [Arguments("a", 1)]
    [Arguments("a\tunknown", 1)]
    public async Task Stop_ack_returns_failure_only_for_failed_or_unrecognized_results(string ack, int expected) {
        var (_, _, exitCode, _) = await RunCliAsync(["stop", AgentId], "", _ => LocalFrame.StopAck(ack));
        await Assert.That(exitCode).IsEqualTo(expected);
    }

    [Test]
    [Arguments("a\tRunning", 1)]
    [Arguments("a\tRunning\t/repo", 0)]
    [Arguments("a\tRunning\t/repo\tagent", 1)]
    [Arguments("a\tRunning\t/repo\tagent\tflow", 1)]
    [Arguments("a\tRunning\t/repo\tagent\t\t", 0)]
    [Arguments("a\tRunning\t/repo\tagent\t\t\textra", 1)]
    [Arguments("a\tRunning\t/repo\nb\tRunning\t/repo\tagent", 1)]
    [Arguments("\tRunning\t/repo", 1)]
    public async Task Stop_all_refuses_the_entire_malformed_table(string table, int expected) {
        var (_, stderr, exitCode, stops) = await RunCliAsync(["stop", "--all", "-y"], table);

        await Assert.That(exitCode).IsEqualTo(expected);
        if (expected == 1) {
            await Assert.That(stops).IsEmpty();
            await Assert.That(stderr).Contains("malformed agent table");
        } else {
            await Assert.That(stops.Select(s => s.Id)).IsEquivalentTo(new[] { "a" });
        }
    }

    [Test]
    public async Task Agent_list_displays_the_flow_identity_and_role() {
        var (stdout, _, exitCode, _) = await RunCliAsync(["ls"], Table);
        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).Contains("flow-1");
        await Assert.That(stdout).Contains("reviewer");
    }

    async Task<(string Stdout, string Stderr, int ExitCode, List<(bool Force, string Id)> Stops)> RunCliAsync(
            string[] args, string table, Func<string, LocalFrame>? stopReply = null,
            string confirmation = "y", int stopRepliesAfter = 0) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(Daemons.Store.SocketPath("stop-test")));
        listener.Listen(16);
        var stops = new ConcurrentBag<(bool Force, string Id)>();
        var stopsArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = ServeAsync();

        using var process = Process.Start(KcapProcess.StartInfo(Daemons.Store, Config.Root,
            ["agent", .. args, "--daemon", "stop-test", "--no-update-check"]))!;
        try {
            var stdout = ReadOutputAsync();
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (await stdout, await stderr, process.ExitCode, stops.ToList());
        } finally {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await timeout.CancelAsync();
            await serving;
        }

        async Task<string> ReadOutputAsync() {
            var output = new StringBuilder();
            var buffer = new char[1];
            while (await process.StandardOutput.ReadAsync(buffer.AsMemory(), ct) > 0) {
                output.Append(buffer[0]);
                if (output.ToString().EndsWith("[y/N] ", StringComparison.Ordinal)) {
                    await process.StandardInput.WriteLineAsync(confirmation);
                    await process.StandardInput.FlushAsync(ct);
                }
            }
            return output.ToString();
        }

        async Task ServeAsync() {
            var connections = new List<Task>();
            try {
                while (!ct.IsCancellationRequested) {
                    var socket = await listener.AcceptAsync(ct);
                    connections.Add(HandleAsync(socket));
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally {
                await Task.WhenAll(connections);
            }
        }

        async Task HandleAsync(Socket socket) {
            using (socket) {
                try {
                    await using var stream = new NetworkStream(socket, ownsSocket: false);
                    var frame = await FrameCodec.ReadAsync(stream, ct);
                    if (frame?.Type == FrameType.List) {
                        await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.AgentList) { Text = table }, ct);
                    } else if (frame?.Type == FrameType.StopV2) {
                        var (force, id) = FrameCodec.StopV2(frame);
                        stops.Add((force, id));
                        if (stops.Count >= stopRepliesAfter) stopsArrived.TrySetResult();
                        await stopsArrived.Task.WaitAsync(ct);
                        await FrameCodec.WriteAsync(stream,
                            stopReply?.Invoke(id) ?? LocalFrame.StopAck($"{id}\tstopped"), ct);
                    } else {
                        throw new InvalidDataException($"Unexpected request: {frame?.Type}");
                    }
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            }
        }
    }
}
