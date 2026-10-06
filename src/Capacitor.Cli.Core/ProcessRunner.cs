using System.Diagnostics;
using System.Text;

namespace Capacitor.Cli.Core;

/// AbandonWait: an external ct cancellation abandons the WAIT only, child keeps running.
/// KillTree: an external ct cancellation kills the child (tree) and awaits its exit first.
public enum CancelMode {
    AbandonWait,
    KillTree,
}

/// Scope of the kill on internal Timeout expiry only — CancelMode's KillTree always kills the tree.
public enum TimeoutKillScope {
    Tree,
    ProcessOnly,
}

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut) {
    /// Set by <see cref="IProcessRunner.RunBoundedAsync"/> when a stream passed its limit; both captures are then empty.
    public bool Oversized { get; init; }
}

public sealed record RunOptions(
    IReadOnlyDictionary<string, string>? EnvOverlay = null, // adds/overrides; rest of env untouched
    TimeSpan? Timeout = null,                                // internal deadline: kills per TimeoutKill scope + awaits on expiry
    CancelMode CancelMode = CancelMode.AbandonWait,
    TimeoutKillScope TimeoutKill = TimeoutKillScope.Tree);

public enum ProcessStreamKind { Stdout, Stderr }

public sealed record StreamedLine(ProcessStreamKind Kind, string Text);

/// No full Stdout/Stderr captures by design — Tail is only a bounded trailing window.
public sealed record StreamingResult(int ExitCode, bool TimedOut, IReadOnlyList<StreamedLine> Tail);

/// Seam over process spawning so process-driven services are testable without touching a real
/// CLI binary. The production implementation (<see cref="ProcessRunner"/>) wraps
/// System.Diagnostics.Process.
public interface IProcessRunner {
    Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct);

    /// <summary>
    /// <see cref="RunAsync"/> with each stream capped at <paramref name="outputLimit"/> UTF-8 bytes. The default only
    /// applies the cap after a full capture, so a fake answers like the real runner without bounding anything.
    /// </summary>
    async Task<ProcessResult> RunBoundedAsync(string fileName, string[] args, RunOptions options, int outputLimit, CancellationToken ct) {
        var result = await RunAsync(fileName, args, options, ct).ConfigureAwait(false);
        return !result.TimedOut && (Encoding.UTF8.GetByteCount(result.Stdout) > outputLimit || Encoding.UTF8.GetByteCount(result.Stderr) > outputLimit)
            ? result with { Stdout = "", Stderr = "", Oversized = true }
            : result;
    }

    /// Cancellation always kills the tree and awaits exit first — unlike RunAsync, ignores RunOptions.CancelMode.
    Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
        Action<StreamedLine> onLine, CancellationToken ct);
}

/// Production IProcessRunner: wraps System.Diagnostics.Process with stdout/stderr capture, an
/// env overlay, an internal timeout, and a per-call cancel mode. <c>RunOptions.Timeout</c> is an
/// internal deadline distinct from <c>ct</c>: on expiry the process (or tree, per
/// <c>RunOptions.TimeoutKill</c>) is killed and awaited, and the result comes back with
/// TimedOut=true rather than throwing. <c>ct</c> cancellation behaves per
/// <c>RunOptions.CancelMode</c>: AbandonWait abandons the WAIT only (a detached <c>daemon start
/// -d</c> keeps running) and still throws OperationCanceledException; KillTree kills the tree and
/// awaits its exit first, then STILL throws — cancellation is cancellation, TimedOut is only for
/// the internal Timeout.
public sealed class ProcessRunner(TimeProvider time) : IProcessRunner {
    public async Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
        var psi = StartInfo(fileName, args, options);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        // CancellationToken.None on both drains: neither `ct` nor the internal timeout ever
        // abandons the pipes — a drain tied to either would stop reading on cancellation and
        // let a detached/killed child block on a full pipe buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        return await CollectAsync(process, stdoutTask, stderrTask, options, CancellationToken.None, ct).ConfigureAwait(false);
    }

    /// Stops reading once either stream passes <paramref name="outputLimit"/> bytes, then kills the tree and awaits its
    /// exit before returning, so the caller never holds more than the limit per stream nor outlives the child.
    public async Task<ProcessResult> RunBoundedAsync(string fileName, string[] args, RunOptions options, int outputLimit, CancellationToken ct) {
        var psi = StartInfo(fileName, args, options);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        // Not disposed: a drain abandoned on the cancellation path may still signal it after this method returns.
        var overflow = new CancellationTokenSource();
        var stdoutTask = DrainAsync(process.StandardOutput.BaseStream, outputLimit, overflow);
        var stderrTask = DrainAsync(process.StandardError.BaseStream, outputLimit, overflow);
        var result = await CollectAsync(process, stdoutTask, stderrTask, options, overflow.Token, ct).ConfigureAwait(false);
        return overflow.IsCancellationRequested && !result.TimedOut ? result with { Stdout = "", Stderr = "", Oversized = true } : result;
    }

    static async Task<string> DrainAsync(Stream stream, int limit, CancellationTokenSource overflow) {
        using var captured = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, CancellationToken.None).ConfigureAwait(false)) > 0) {
            if (captured.Length + read > limit) {
                overflow.Cancel();
                return "";
            }
            captured.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(captured.GetBuffer(), 0, (int)captured.Length);
    }

    async Task<ProcessResult> CollectAsync(Process process, Task<string> stdoutTask, Task<string> stderrTask, RunOptions options,
            CancellationToken overflow, CancellationToken ct) {
        using var timeoutCts = options.Timeout is { } timeout ? new CancellationTokenSource(timeout, time) : null;
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts?.Token ?? CancellationToken.None, overflow);

        try {
            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            if (ct.IsCancellationRequested) {
                if (options.CancelMode == CancelMode.KillTree)
                    await KillAndAwaitAsync(process).ConfigureAwait(false);

                // The drains outlive this method on the abandoned-wait path; observed so a later
                // fault is not an unobserved task exception.
                Observe(stdoutTask);
                Observe(stderrTask);
                throw;
            }

            if (overflow.IsCancellationRequested) {
                await KillAndAwaitAsync(process).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: false);
            }

            await KillAndAwaitAsync(process, options.TimeoutKill == TimeoutKillScope.Tree).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: true);
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: false);
    }

    // CreateNoWindow: the desktop app is a GUI process, and on Windows a console child of one gets a
    // console window of its own — every CLI call would flash a terminal. Output is redirected, so
    // nothing needs the window; elsewhere the flag is ignored.
    internal static ProcessStartInfo StartInfo(string fileName, string[] args, RunOptions options) {
        var psi = new ProcessStartInfo(fileName) {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (options.EnvOverlay is not null)
            foreach (var (key, value) in options.EnvOverlay) psi.Environment[key] = value;
        return psi;
    }

    const int TailLimit = 500;

    public async Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) {
        var psi = StartInfo(fileName, args, options);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var tailLock = new object();
        var tail = new Queue<StreamedLine>(TailLimit + 1);

        void Record(StreamedLine line) {
            try { onLine(line); }
            catch (Exception ex) { Console.Error.WriteLine($"kcap: streaming callback threw for '{fileName}': {ex}"); }

            lock (tailLock) {
                tail.Enqueue(line);
                if (tail.Count > TailLimit) tail.Dequeue();
            }
        }

        // Line-buffered per stream — no cross-stream ordering promise; drains to EOF even under kill.
        async Task PumpAsync(TextReader reader, ProcessStreamKind kind) {
            string? line;
            while ((line = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
                Record(new StreamedLine(kind, line));
        }

        var stdoutTask = PumpAsync(process.StandardOutput, ProcessStreamKind.Stdout);
        var stderrTask = PumpAsync(process.StandardError, ProcessStreamKind.Stderr);

        using var timeoutCts = options.Timeout is { } timeout ? new CancellationTokenSource(timeout, time) : null;
        using var waitCts = timeoutCts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            await process.WaitForExitAsync(waitCts?.Token ?? ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            if (ct.IsCancellationRequested) {
                // Streaming always kills the tree on cancellation, ignoring RunOptions.CancelMode.
                await KillAndAwaitAsync(process).ConfigureAwait(false);
                // Awaited, not fire-and-forget: the pumps end at EOF once the tree is killed
                // (same as the timeout arm below) — a fire-and-forget Observe let a callback
                // fire AFTER this method had already thrown OCE, racing caller cleanup.
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }

            await KillAndAwaitAsync(process, options.TimeoutKill == TimeoutKillScope.Tree).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            lock (tailLock) return new StreamingResult(process.ExitCode, TimedOut: true, tail.ToArray());
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        lock (tailLock) return new StreamingResult(process.ExitCode, TimedOut: false, tail.ToArray());
    }

    static async Task KillAndAwaitAsync(Process process, bool entireProcessTree = true) {
        try { process.Kill(entireProcessTree); }
        catch (InvalidOperationException) { /* already exited */ }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    static void Observe(Task task) => task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
