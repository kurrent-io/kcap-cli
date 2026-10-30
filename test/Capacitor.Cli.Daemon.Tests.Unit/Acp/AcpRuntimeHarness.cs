using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>An <see cref="AcpHostedAgentRuntime"/> driven by a <see cref="FakeAcpAgent"/>, for tests that read its envelopes.</summary>
sealed class AcpRuntimeHarness : IAsyncDisposable {
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    public FakeAcpAgent            Fake    { get; } = new();
    public AcpHostedAgentRuntime   Runtime { get; }
    public CancellationTokenSource Cts     { get; } = new();

    Task _fakeRunTask = Task.CompletedTask;

    public AcpRuntimeHarness(Func<AcpNotification, IReadOnlyList<AcpEventEnvelope>>? turnUsageReader = null) {
        var conn = new AcpConnection(Fake.ClientWriteStream, Fake.ClientReadStream, NullLogger.Instance);
        Runtime = new AcpHostedAgentRuntime(conn, new FakeAcpProcess(), NullLogger.Instance, TimeProvider.System,
            turnUsageReader: turnUsageReader);
    }

    public async Task StartAsync() {
        _fakeRunTask = Fake.RunAsync(Cts.Token);
        await Runtime.StartAsync("/abs/worktree", "hi", Cts.Token).WaitAsync(HangGuard);
    }

    public async Task<AcpEventEnvelope> ReadUntilAsync(Func<AcpEventEnvelope, bool> match) {
        while (true) {
            var envelope = await Runtime.Envelopes.ReadAsync().AsTask().WaitAsync(HangGuard);
            if (match(envelope)) return envelope;
        }
    }

    public async ValueTask DisposeAsync() {
        await Cts.CancelAsync();
        try { await _fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await Runtime.DisposeAsync();
        await Fake.DisposeAsync();
        Cts.Dispose();
    }

    sealed class FakeAcpProcess : IAcpProcess {
        public int  Pid       { get; init; } = 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }
        public Task WaitForExitAsync(TimeSpan? timeout = null) => timeout is { } t ? Task.Delay(t) : Task.Delay(Timeout.InfiniteTimeSpan);
        public Task TerminateAsync(TimeSpan? timeout = null) { HasExited = true; ExitCode = 0; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
