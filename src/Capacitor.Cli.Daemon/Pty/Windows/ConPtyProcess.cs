using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Capacitor.Cli.Daemon.Pty;
using Microsoft.Win32.SafeHandles;
using static Capacitor.Cli.Daemon.Pty.Windows.ConPtyInterop;

namespace Capacitor.Cli.Daemon.Pty.Windows;

public sealed class ConPtyProcess : IPtyProcess {
    readonly IntPtr                  _hPC;
    readonly IntPtr                  _hProcess;
    readonly IntPtr                  _hOutputPipe;
    readonly FileStream              _outputStream;
    readonly FileStream              _inputStream;
    readonly SafeFileHandle          _jobHandle; // SafeHandle so a thrown exception before the ctor still gets cleaned up by the caller's catch path
    readonly CancellationTokenSource _cts = new();
    bool                             _disposed;
    int                              _pcClosed;

    public int  Pid       { get; private set; }
    public bool HasExited { get; private set; }
    public int? ExitCode  { get; private set; }

    // Test-only seam: exposes the raw job handle value so tests can QUERY the job's limit flags
    // (a read-only structural proof that breakaway is impossible). Does not transfer ownership —
    // the job is still torn down exclusively via _jobHandle.Dispose() in DisposeAsync.
    // IRON RULE: a test must NEVER AssignProcessToJobObject(this handle, self). This job carries
    // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so joining the test host and then disposing this
    // process would close the last handle and have the OS kill the test host.
    internal IntPtr JobHandleForTests => _jobHandle.DangerousGetHandle();

    ConPtyProcess(IntPtr hPC, IntPtr hProcess, IntPtr hOutputPipe, FileStream outputStream, FileStream inputStream, SafeFileHandle jobHandle) {
        _hPC          = hPC;
        _hProcess     = hProcess;
        _hOutputPipe  = hOutputPipe;
        _outputStream = outputStream;
        _inputStream  = inputStream;
        _jobHandle    = jobHandle;
    }

    static (string command, bool isCmd) ResolveCommand(string command) {
        if (Path.HasExtension(command) && File.Exists(command)) {
            return (command, command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
        }

        if (Path.IsPathRooted(command)) {
            foreach (var ext in new[] { ".exe", ".cmd" }) {
                var candidate = command + ext;

                if (File.Exists(candidate)) {
                    return (candidate, ext == ".cmd");
                }
            }

            return (command, false);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            var exact = Path.Combine(dir, command);

            if (File.Exists(exact)) {
                return (exact, false);
            }

            foreach (var ext in new[] { ".exe", ".cmd" }) {
                var candidate = Path.Combine(dir, command + ext);

                if (File.Exists(candidate)) {
                    return (candidate, ext == ".cmd");
                }
            }
        }

        return (command, false);
    }

    static IntPtr BuildEnvironmentBlock(Dictionary<string, string>? extraEnv) {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables()) {
            if (entry is { Key: string key, Value: string value }) {
                env[key] = value;
            }
        }

        env["TERM"] = "xterm-256color";

        foreach (var key in PtyEnvScrub.ClaudeSessionVars) {
            env.Remove(key);
        }

        // Clear any hosted-agent identity/routing the daemon may have inherited (e.g.
        // it was started from inside a kcap-tracked session) so the spawned agent gets
        // ONLY what extraEnv sets: hosted launches re-add these below; private local
        // launches deliberately leave them unset (no mis-tag, native permissions).
        foreach (var key in PtyEnvScrub.HostedAgentVars) {
            env.Remove(key);
        }

        // Parity with UnixPtyProcess: never leak daemon supervision state into spawned
        // children. Auto-restart is out of scope on Windows.
        foreach (var key in PtyEnvScrub.DaemonSupervisionVars) {
            env.Remove(key);
        }

        if (extraEnv is not null) {
            foreach (var (key, value) in extraEnv) {
                env[key] = value;
            }
        }

        var sb = new StringBuilder();

        foreach (var (key, value) in env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) {
            sb.Append(key);
            sb.Append('=');
            sb.Append(value);
            sb.Append('\0');
        }

        sb.Append('\0');

        var chars     = sb.ToString().ToCharArray();
        var byteCount = chars.Length * 2;
        var block     = Marshal.AllocHGlobal(byteCount);
        Marshal.Copy(chars, 0, block, chars.Length);

        return block;
    }

    public static ConPtyProcess Spawn(
            string                      command,
            string[]                    args,
            string                      cwd,
            Dictionary<string, string>? extraEnv = null,
            ushort                      cols     = 120,
            ushort                      rows     = 40
        ) {
        var (resolvedCommand, isCmd) = ResolveCommand(command);

        var cmdLine = new StringBuilder();

        if (isCmd) {
            cmdLine.Append("cmd.exe /c ");
        }

        cmdLine.Append(QuoteArg(resolvedCommand));

        foreach (var arg in args) {
            cmdLine.Append(' ');
            cmdLine.Append(QuoteArg(arg));
        }

        var pipeSa = new ConPtyInterop.SECURITY_ATTRIBUTES {
            nLength        = Marshal.SizeOf<ConPtyInterop.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        if (!CreatePipe(out var ptyInputRead, out var ptyInputWrite, ref pipeSa, 0)) {
            throw new InvalidOperationException($"CreatePipe (input) failed: {Marshal.GetLastWin32Error()}");
        }

        if (!CreatePipe(out var ptyOutputRead, out var ptyOutputWrite, ref pipeSa, 0)) {
            throw new InvalidOperationException($"CreatePipe (output) failed: {Marshal.GetLastWin32Error()}");
        }

        var size = new ConPtyInterop.COORD { X = (short)cols, Y = (short)rows };
        var hr   = CreatePseudoConsole(size, ptyInputRead, ptyOutputWrite, 0, out var hPC);

        if (hr != 0) {
            throw new InvalidOperationException($"CreatePseudoConsole failed: HRESULT 0x{hr:X8}");
        }

        CloseHandle(ptyInputRead);
        CloseHandle(ptyOutputWrite);

        // §4.1: bind the child to a KILL_ON_JOB_CLOSE job at creation time (via the
        // PROC_THREAD_ATTRIBUTE_JOB_LIST attribute below), not by AssignProcessToJobObject
        // after the fact — there is no window where the child exists uncontained.
        var hJob = CreateJobObjectW(IntPtr.Zero, null);

        if (hJob == IntPtr.Zero) {
            throw new InvalidOperationException($"CreateJobObjectW failed: {Marshal.GetLastWin32Error()}");
        }

        var limitInfo = new ConPtyInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
            BasicLimitInformation = new ConPtyInterop.JOBOBJECT_BASIC_LIMIT_INFORMATION {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        if (!SetInformationJobObject(
                hJob, JobObjectExtendedLimitInformation, ref limitInfo,
                (uint)Marshal.SizeOf<ConPtyInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())) {
            var err = Marshal.GetLastWin32Error();
            CloseHandle(hJob);

            throw new InvalidOperationException($"SetInformationJobObject failed: {err}");
        }

        var jobHandle = new SafeFileHandle(hJob, ownsHandle: true);

        var attrListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attrListSize);
        var attrList = Marshal.AllocHGlobal(attrListSize);

        if (!InitializeProcThreadAttributeList(attrList, 2, 0, ref attrListSize)) {
            Marshal.FreeHGlobal(attrList);
            jobHandle.Dispose();

            throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");
        }

        if (!UpdateProcThreadAttribute(
                attrList,
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                hPC,
                IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero
            )) {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            jobHandle.Dispose();

            throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");
        }

        // PROC_THREAD_ATTRIBUTE_JOB_LIST's value is a pointer to an ARRAY of job handles — one
        // element here. The child becomes a job member at the instant CreateProcessW succeeds:
        // there is no suspended-then-assign window (AssignProcessToJobObject after the fact
        // would have one).
        var jobArray = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(jobArray, 0, hJob);

        if (!UpdateProcThreadAttribute(
                attrList,
                0,
                PROC_THREAD_ATTRIBUTE_JOB_LIST,
                jobArray,
                IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero
            )) {
            var err = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(jobArray);
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            jobHandle.Dispose();

            throw new InvalidOperationException($"UpdateProcThreadAttribute(JOB_LIST) failed: {err}");
        }

        var si = new ConPtyInterop.STARTUPINFOEXW();
        si.StartupInfo.cb      = Marshal.SizeOf<ConPtyInterop.STARTUPINFOEXW>();
        si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        si.lpAttributeList     = attrList;

        var envBlock = BuildEnvironmentBlock(extraEnv);

        try {
            const uint creationFlags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT;

            if (!CreateProcessW(
                    null,
                    cmdLine.ToString(),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    creationFlags,
                    envBlock,
                    cwd,
                    ref si,
                    out var pi
                )) {
                // CreateProcessW failure (bad path, access-denied, blocked nesting — all common)
                // means no child was ever created, so the job has no members. Close its handle
                // BEFORE throwing: the outer finally only frees the HGlobal buffers, never the
                // job kernel handle, so without this the job would leak to GC finalization.
                var err = Marshal.GetLastWin32Error();
                jobHandle.Dispose();

                throw new InvalidOperationException($"CreateProcessW failed: {err}");
            }

            try {
                CloseHandle(pi.hThread);

                var outputSafeHandle = new SafeFileHandle(ptyOutputRead, ownsHandle: true);
                var inputSafeHandle  = new SafeFileHandle(ptyInputWrite, ownsHandle: true);
                var outputStream     = new FileStream(outputSafeHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
                var inputStream      = new FileStream(inputSafeHandle, FileAccess.Write, bufferSize: 4096, isAsync: false);

                return new(hPC, pi.hProcess, ptyOutputRead, outputStream, inputStream, jobHandle) { Pid = pi.dwProcessId };
            } catch {
                // Post-create failure: the child exists but we can't finish wiring it up. Kill
                // it via the job (closes over descendants too) and confirm death before
                // propagating — the caller's teardown/quarantine machinery must never see an
                // ambiguous "maybe spawned".
                TerminateJobObject(hJob, 1);

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

                while (DateTime.UtcNow < deadline) {
                    if (GetExitCodeProcess(pi.hProcess, out var code) && code != STILL_ACTIVE) break;
                    Thread.Sleep(50);
                }

                CloseHandle(pi.hProcess);
                jobHandle.Dispose();

                throw;
            }
        } finally {
            Marshal.FreeHGlobal(jobArray);
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            Marshal.FreeHGlobal(envBlock);
        }
    }

    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        using var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var       buf        = new byte[4096];
        var       emptyPolls = 0;

        while (!linked.Token.IsCancellationRequested) {
            if (!PeekNamedPipe(_hOutputPipe, IntPtr.Zero, 0, IntPtr.Zero, out var avail, IntPtr.Zero)) {
                CheckExited();

                yield break;
            }

            if (avail > 0) {
                emptyPolls = 0;
                var toRead = (uint)Math.Min(avail, buf.Length);

                if (!ReadFile(_hOutputPipe, buf, toRead, out var read, IntPtr.Zero) || read == 0) {
                    CheckExited();

                    yield break;
                }

                var data = new byte[read];
                Array.Copy(buf, data, (int)read);

                yield return data;
            } else {
                CheckExited();

                if (HasExited) {
                    emptyPolls++;

                    if (emptyPolls > 5) {
                        if (Interlocked.Exchange(ref _pcClosed, 1) == 0) {
                            ClosePseudoConsole(_hPC);
                        }

                        if (PeekNamedPipe(_hOutputPipe, IntPtr.Zero, 0, IntPtr.Zero, out var finalAvail, IntPtr.Zero) && finalAvail > 0) {
                            if (ReadFile(_hOutputPipe, buf, (uint)Math.Min(finalAvail, buf.Length), out var finalRead, IntPtr.Zero) && finalRead > 0) {
                                var finalData = new byte[finalRead];
                                Array.Copy(buf, finalData, (int)finalRead);

                                yield return finalData;
                            }
                        }

                        break;
                    }
                }

                try { await Task.Delay(10, linked.Token); } catch (OperationCanceledException) { yield break; }
            }
        }
    }

    public Task WriteAsync(string input) {
        var bytes = Encoding.UTF8.GetBytes(input);

        return Task.Run(() => {
                _inputStream.Write(bytes, 0, bytes.Length);
                _inputStream.Flush();
            }
        );
    }

    public Task WriteAsync(byte[] data) {
        return Task.Run(() => {
                _inputStream.Write(data, 0, data.Length);
                _inputStream.Flush();
            }
        );
    }

    public void Resize(ushort cols, ushort rows) {
        var size = new ConPtyInterop.COORD { X = (short)cols, Y = (short)rows };
        ResizePseudoConsole(_hPC, size);
    }

    public void SendInterrupt() {
        if (!HasExited) {
            try {
                _inputStream.WriteByte(0x03);
                _inputStream.Flush();
            } catch {
                /* process may have exited */
            }
        }
    }

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        if (HasExited) {
            return;
        }

        SendInterrupt();

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (!HasExited && DateTime.UtcNow < deadline) {
            CheckExited();
            if (!HasExited) {
                await Task.Delay(100);
            }
        }

        if (!HasExited) {
            TerminateProcess(_hProcess, 1);
            CheckExited();
        }
    }

    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        if (HasExited) {
            return;
        }

        var sw = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);

        while (!HasExited && sw.Elapsed < limit) {
            CheckExited();

            if (!HasExited) {
                await Task.Delay(50);
            }
        }
    }

    void CheckExited() {
        if (GetExitCodeProcess(_hProcess, out var exitCode) && exitCode != STILL_ACTIVE) {
            HasExited = true;
            ExitCode  = (int)exitCode;
        }
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) {
            return;
        }

        _disposed = true;

        await _cts.CancelAsync();

        if (!HasExited) {
            await TerminateAsync();
        }

        try { await _outputStream.DisposeAsync(); } catch { }

        try { await _inputStream.DisposeAsync(); } catch { }

        if (Interlocked.Exchange(ref _pcClosed, 1) == 0) {
            ClosePseudoConsole(_hPC);
        }

        CloseHandle(_hProcess);
        _jobHandle.Dispose(); // last handle to the job closes here → OS kills leader + all descendants
        _cts.Dispose();
    }

    static string QuoteArg(string arg) {
        if (arg.Length > 0 && !arg.AsSpan().ContainsAny(' ', '\t', '"')) {
            return arg;
        }

        var sb = new StringBuilder();
        sb.Append('"');

        for (var i = 0; i < arg.Length; i++) {
            var backslashes = 0;

            while (i < arg.Length && arg[i] == '\\') {
                backslashes++;
                i++;
            }

            if (i == arg.Length) {
                sb.Append('\\', backslashes * 2);

                break;
            }

            if (arg[i] == '"') {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            } else {
                sb.Append('\\', backslashes);
                sb.Append(arg[i]);
            }
        }

        sb.Append('"');

        return sb.ToString();
    }
}

public class WinPtyProcessFactory : IPtyProcessFactory {
    public IPtyProcess Spawn(
            string                      command,
            string[]                    args,
            string                      cwd,
            Dictionary<string, string>? extraEnv = null,
            ushort                      cols     = 120,
            ushort                      rows     = 40
        )
        => ConPtyProcess.Spawn(command, args, cwd, extraEnv, cols, rows);
}
