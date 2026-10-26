using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Commands.Harness;

/// <summary><c>kcap statusline --account &lt;id&gt;</c>, the command wiring installs as a Claude account's
/// status line: records the payload's rate limits, then runs the user's original command unchanged.
/// Dispatched before any other startup, so the original sees exactly the environment Claude gave.</summary>
public static class ClaudeStatusLineEntry {
    // Covers parse, registry read and the locked write together; past it the original runs regardless.
    static readonly TimeSpan CaptureBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>Never throws: it runs ahead of the top-level handler, and a crash trace would replace the status line.</summary>
    public static async Task<int> RunAsync(string[] args, Func<AccountStore> accounts, CancellationToken ct) {
        try {
            return await RunAsync(args, accounts(), ct);
        } catch (Exception) {
            return 0;
        }
    }

    static async Task<int> RunAsync(string[] args, AccountStore accounts, CancellationToken ct) {
        var signaled      = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrations = RegisterSignals(signaled);
        await using var cancel = ct.Register(() => signaled.TrySetResult(130));

        try {
            var read = ReadStdinAsync();
            if (await Task.WhenAny(read, signaled.Task) == signaled.Task) return await signaled.Task;

            var stdin   = await read;
            var account = FindAccount(accounts, AccountArgument(args));

            if (account is not null) await CaptureAsync(accounts, account, stdin);

            if (account?.StatusLine?.Original is not { } original) return 0;
            if (signaled.Task.IsCompleted) return await signaled.Task;

            return await RunOriginalAsync(original, stdin, signaled.Task);
        } finally {
            foreach (var registration in registrations) registration.Dispose();
        }
    }

    static string? AccountArgument(string[] args) {
        var at = Array.IndexOf(args, "--account");
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    static VendorAccount? FindAccount(AccountStore accounts, string? id) {
        if (id is null) return null;

        try {
            return accounts.TryLoad()?.Accounts.FirstOrDefault(a => a.Id == id && a.Vendor == HarnessId.Claude);
        } catch (Exception) {
            return null;
        }
    }

    static async Task<byte[]> ReadStdinAsync() {
        try {
            await using var input  = Console.OpenStandardInput();
            using var       buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            return buffer.ToArray();
        } catch (IOException) {
            return [];
        }
    }

    static async Task CaptureAsync(AccountStore accounts, VendorAccount account, byte[] stdin) {
        try {
            await Task.Run(() => Capture(accounts, account, stdin)).WaitAsync(CaptureBudget, TimeProvider.System);
        } catch (Exception) {
            // Recording is a side output: a slow file system or a bad payload never costs the status line.
        }
    }

    static void Capture(AccountStore accounts, VendorAccount account, byte[] stdin) {
        if (stdin.Length == 0) return;

        using var document = JsonDocument.Parse(stdin);
        var payload = document.RootElement;
        var now     = TimeProvider.System.GetUtcNow();

        if (ClaudeStatusLineReading.Apply(ReadingFile.Empty(account.Generation), payload, now) is null) return;

        accounts.Readings.TryRecord(
            account.Id,
            account.Generation,
            payload.Str("session_id"),
            held => ClaudeStatusLineReading.Apply(held, payload, now) ?? held
        );
    }

    // No WorkingDirectory and no environment edits: the child inherits both from this process.
    static async Task<int> RunOriginalAsync(string original, byte[] stdin, Task<int> signaled) {
        var shell = ClaudeStatusLineCommand.PosixShellPath(Environment.GetEnvironmentVariable);
        var psi   = new ProcessStartInfo(shell ?? "powershell") {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        if (shell is not null) {
            psi.ArgumentList.Add("-c");
        } else {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
        }
        psi.ArgumentList.Add(original);

        Process child;
        try {
            child = Process.Start(psi) ?? throw new InvalidOperationException();
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) {
            return 127;
        }

        using var _ = child;

        await using var stdout = Console.OpenStandardOutput();
        await using var stderr = Console.OpenStandardError();

        var copies = Task.WhenAll(
            Pipe(child.StandardOutput.BaseStream, stdout),
            Pipe(child.StandardError.BaseStream, stderr),
            Feed(child.StandardInput.BaseStream, stdin)
        );

        // A grandchild holding the pipes open keeps the copies pending past the child's exit, so a
        // signal must still be able to end the wait.
        var finished = Task.WhenAll(child.WaitForExitAsync(), copies);
        if (await Task.WhenAny(finished, signaled) == signaled) {
            Kill(child);
            return await signaled;
        }

        try {
            await finished;
        } catch (Exception) {
            // The child has exited: a failed copy loses output, never the original's status.
        }
        return child.ExitCode;
    }

    static async Task Feed(Stream input, byte[] bytes) {
        try {
            await input.WriteAsync(bytes);
        } catch (IOException) {
            // The original exited or closed stdin without reading it all.
        } finally {
            try { await input.DisposeAsync(); } catch (IOException) { }
        }
    }

    static async Task Pipe(Stream from, Stream to) {
        try {
            await from.CopyToAsync(to);
            await to.FlushAsync();
        } catch (IOException) {
            // Claude closed our output: nothing left to show it on.
        }
    }

    // The original shares Claude's process group, so its tree is killed without the runtime's SIGSTOP.
    static void Kill(Process child) {
        try {
            ProcessTree.Kill(child);
        } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) {
            // Already gone.
        }
    }

    static List<PosixSignalRegistration> RegisterSignals(TaskCompletionSource<int> signaled) {
        var registrations = new List<PosixSignalRegistration>();

        foreach (var (signal, number) in new[] { (PosixSignal.SIGTERM, 15), (PosixSignal.SIGINT, 2), (PosixSignal.SIGHUP, 1) }) {
            try {
                registrations.Add(PosixSignalRegistration.Create(signal, ctx => {
                    ctx.Cancel = true;
                    signaled.TrySetResult(128 + number);
                }));
            } catch (Exception ex) when (ex is PlatformNotSupportedException or IOException) {
                // Not deliverable here.
            }
        }

        return registrations;
    }
}
