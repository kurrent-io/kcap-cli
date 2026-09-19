using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Tests.Unit.NativeTestHost;

/// <summary>
/// Times a keystroke's echo through one PTY while more idle PTYs than the pool's worker minimum
/// are being drained. The PTYs stay in cooked mode, so the kernel echoes each byte itself and the
/// round trip measures only this process's own scheduling. Everything that measures runs on the
/// main thread: a probe that needed a pool worker to take its reading would time itself.
/// </summary>
static class PtyInputLatencyProbe {
    const int IdleBeyondWorkerMinimum = 8;
    const int Samples                 = 6;

    static readonly TimeSpan Settle    = TimeSpan.FromMilliseconds(150);
    static readonly TimeSpan SampleGap = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan EchoWait  = TimeSpan.FromSeconds(30);

    public static int Run() {
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        var count = minWorkers + IdleBeyondWorkerMinimum;

        using var spawner = new UnixSpawnerThread();
        using var echoes  = new BlockingCollection<byte>();
        using var stop    = new CancellationTokenSource();
        var       factory = new UnixPtyProcessFactory(spawner, TimeProvider.System);
        var       ptys    = new List<IPtyProcess>(count);

        try {
            for (var i = 0; i < count; i++) ptys.Add(factory.Spawn("/bin/cat", [], AppContext.BaseDirectory));

            _ = DrainAsync(ptys[0], echoes, stop.Token);
            foreach (var pty in ptys.Skip(1)) _ = DrainAsync(pty, null, stop.Token);

            Thread.Sleep(Settle);

            Console.WriteLine($"MIN_WORKERS={minWorkers}");
            Console.WriteLine($"PTYS={count}");

            for (var sample = 0; sample < Samples; sample++) {
                var key     = (byte)('a' + sample);
                var started = Stopwatch.GetTimestamp();
                _ = ptys[0].WriteAsync([key]);

                if (!WaitForEcho(echoes, key)) {
                    Console.Error.WriteLine($"FAIL: no echo of sample {sample} within {EchoWait}");

                    return 1;
                }

                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Console.WriteLine($"LATENCY_MS={elapsed.ToString("F3", CultureInfo.InvariantCulture)}");
                Thread.Sleep(SampleGap);
            }

            Console.WriteLine($"POOL_THREADS={ThreadPool.ThreadCount}");

            return 0;
        } finally {
            stop.Cancel();
            Task.WhenAll(ptys.Select(pty => pty.DisposeAsync().AsTask())).GetAwaiter().GetResult();
        }
    }

    static bool WaitForEcho(BlockingCollection<byte> echoes, byte key) {
        var started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < EchoWait) {
            if (echoes.TryTake(out var echoed, EchoWait) && echoed == key) return true;
        }

        return false;
    }

    static async Task DrainAsync(IPtyProcess pty, BlockingCollection<byte>? echoes, CancellationToken ct) {
        await foreach (var chunk in pty.ReadOutputAsync(ct)) {
            if (echoes is null) continue;

            foreach (var echoed in chunk) echoes.Add(echoed, ct);
        }
    }
}
