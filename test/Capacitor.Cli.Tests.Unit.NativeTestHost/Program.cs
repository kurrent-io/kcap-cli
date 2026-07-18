using Capacitor.Cli.Daemon.Pty.Unix;

// A tiny, disposable process the OUTER test can kill and observe from the outside — the
// mechanism the PDEATHSIG and spawner-thread-FailFast tests need (you cannot safely assert
// "my own process just crashed" from inside the process doing the crashing).
var mode = args.Length > 0 ? args[0] : "";

switch (mode) {
    case "spawn-dummy": {
        // Exercises the REAL production entry point end-to-end (not a shortcut into pty_spawn
        // directly) so this proves the actual daemon spawn path, including the spawner thread
        // Task 5 wires UnixPtyProcessFactory through.
        // No disposal of the spawner thread here — this whole process is a disposable one-shot
        // the outer test kills (SIGKILL) to observe PDEATHSIG, so a graceful Dispose() never runs
        // and never needs to.
        var factory = new UnixPtyProcessFactory(new UnixSpawnerThread());
        var proc    = factory.Spawn("sleep", ["30"], Directory.GetCurrentDirectory());
        Console.WriteLine($"PID={proc.Pid}");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite); // block until the outer test kills THIS process
        break;
    }
    case "crash-spawner": {
        // Forces the spawner thread's underlying loop to throw unexpectedly, exercising the
        // Environment.FailFast policy — the outer test asserts THIS process dies loudly rather
        // than lingering half-broken.
        var thread = new UnixSpawnerThread();
        thread.CrashForTest(); // test-only seam added in Step 3
        Console.WriteLine("READY");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        break;
    }
    default:
        Console.Error.WriteLine($"unknown mode: {mode}");
        return 1;
}

return 0;
