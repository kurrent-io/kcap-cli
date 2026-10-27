using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>
/// <see cref="IMuseMspProcess"/> over a real <see cref="Process"/>. Stdin stays open for the child's
/// lifetime; writes are serialized under <see cref="_stdinGate"/>, and the pid is captured at
/// construction because <see cref="Process.Id"/> throws once the object is disposed.
/// </summary>
internal sealed partial class MuseMspProcess : IMuseMspProcess {
    /// <summary>Over-cap stderr is dropped, never rotated: the explanation of a failed launch is at the start.</summary>
    internal const int DiagnosticsCap = 4096;

    readonly Process                 _process;
    readonly ILogger                 _logger;
    readonly TimeProvider            _time;
    readonly CancellationTokenSource _stderrDrainCts = new();
    readonly Task                    _stderrDrainTask;
    readonly Lock                    _diagnosticsGate = new();
    readonly StringBuilder           _diagnostics     = new();
    readonly SemaphoreSlim           _stdinGate       = new(1, 1);

    int _disposed;

    bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal MuseMspProcess(ProcessStartInfo psi, ILogger logger, TimeProvider time)
        : this(Process.Start(psi) ?? throw new InvalidOperationException(
                   $"muse_msp_spawn_failed: '{psi.FileName}' did not start (Process.Start returned null)."),
               logger, time) { }

    internal MuseMspProcess(Process process, ILogger logger, TimeProvider time) {
        _process = process;
        _logger  = logger;
        _time    = time;
        Pid      = SafePid(process);

        _stderrDrainTask = DrainStderrAsync(_stderrDrainCts.Token);
    }

    public int  Pid       { get; }
    public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }

    public int? ExitCode {
        get {
            try {
                return _process.HasExited ? _process.ExitCode : null;
            } catch {
                return null;
            }
        }
    }

    public string? Diagnostics {
        get { lock (_diagnosticsGate) return _diagnostics.Length == 0 ? null : _diagnostics.ToString(); }
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct) {
        while (true) {
            string? line;

            try {
                line = await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            } catch (IOException) {
                break;   // pipe torn down by a kill mid-read: EOF
            } catch (ObjectDisposedException) {
                break;   // stream disposed by a racing DisposeAsync: EOF
            }

            if (line is null) break;

            yield return line;
        }
    }

    /// <summary>Disposal is checked before and after taking the gate: a writer queued when
    /// <see cref="DisposeAsync"/> ran must not write into the process it just killed.</summary>
    public async Task WriteLineAsync(string json, CancellationToken ct) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        await _stdinGate.WaitAsync(ct).ConfigureAwait(false);

        try {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            await _process.StandardInput.WriteAsync((json + "\n").AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        } finally {
            _stdinGate.Release();
        }
    }

    /// <summary>An undrained stderr pipe fills at ~64KB and blocks the child's next write, wedging
    /// every later turn. The text is never logged: it can carry prompt fragments, paths or auth detail.</summary>
    async Task DrainStderrAsync(CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                var line = await _process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);

                if (line is null) break;
                if (line.Length == 0) continue;

                Capture(line);
                LogStderrShape(line.Length);
            }
        } catch (OperationCanceledException) {
        } catch (IOException) {
        } catch (ObjectDisposedException) {
        }
    }

    void Capture(string line) {
        lock (_diagnosticsGate) AppendCapped(_diagnostics, line);
    }

    /// <summary>A line that would cross the cap is cut at it.</summary>
    internal static void AppendCapped(StringBuilder diagnostics, string line) {
        var room = DiagnosticsCap - diagnostics.Length;
        if (room <= 0) return;

        if (line.Length < room) diagnostics.Append(line).Append('\n');
        else diagnostics.Append(line, 0, room);
    }

    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        try {
            if (timeout is { } t) {
                using var cts = new CancellationTokenSource(t, _time);

                try {
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                }
            } else {
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        } catch {
            // Already exited or disposed.
        }
    }

    public async Task CloseInputAsync(TimeSpan timeout) {
        if (IsDisposed) return;

        bool acquired;

        try {
            acquired = await _stdinGate.WaitAsync(timeout).ConfigureAwait(false);
        } catch {
            return;
        }

        if (!acquired) return;

        try {
            if (!IsDisposed) _process.StandardInput.Close();
        } catch {
            // The child already exited, or its stdin is already gone.
        } finally {
            _stdinGate.Release();
        }
    }

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        try {
            if (_process.HasExited) return;

            ProcessTree.Kill(_process);
        } catch {
            return;   // already exited or disposed
        }

        await WaitForExitAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Kill first: a writer already inside the stdin gate fails fast on the broken pipe instead
        // of hanging on a pipe that will never drain.
        try {
            if (!_process.HasExited) ProcessTree.Kill(_process);
        } catch {
        }

        try {
            await _stderrDrainCts.CancelAsync().ConfigureAwait(false);
        } catch {
        }

        try {
            await _stderrDrainTask.WaitAsync(TimeSpan.FromSeconds(2), _time).ConfigureAwait(false);
        } catch {
        }

        _stderrDrainCts.Dispose();

        // _stdinGate is deliberately not disposed: disposing a SemaphoreSlim does not release
        // outstanding async waiters, so a queued writer would hang forever. WriteLineAsync's
        // disposal checks turn those writers away instead.
        try {
            _process.Dispose();
        } catch {
        }
    }

    static int SafePid(Process process) {
        try {
            return process.Id;
        } catch {
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Muse MSP stderr: {Length} chars")]
    partial void LogStderrShape(int length);
}
