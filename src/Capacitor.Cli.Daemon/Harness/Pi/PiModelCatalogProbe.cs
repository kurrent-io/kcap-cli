using System.Diagnostics;
using System.Text.Json;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// Asks an installed `pi` for the models it can launch here, over its RPC mode, and maps them to
/// `provider/id` values. The answer is Pi's own auth-filtered list; the daemon never reads Pi's
/// auth or models files.
internal static partial class PiModelCatalogProbe {
    internal const string Command = "get_available_models";

    internal static readonly TimeSpan Deadline  = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    const int StderrLogCap = 512;

    internal static string DirectoryFor(string stateDir) => Path.Combine(stateDir, "pi-probe");

    /// Emptied before every run: Pi reads a `.pi/` in its cwd as project config and renames its
    /// commands directory at startup. Links are removed, never followed, so emptying cannot reach
    /// outside the state directory.
    internal static void PrepareDirectory(string dir) {
        var root = new DirectoryInfo(dir);
        if (root.LinkTarget is not null) {
            if (root.Exists) root.Delete();
            else File.Delete(dir);
        } else if (root.Exists) {
            foreach (var entry in root.EnumerateFileSystemInfos()) {
                if (entry is DirectoryInfo { LinkTarget: null } sub) sub.Delete(recursive: true);
                else if (entry is DirectoryInfo link) link.Delete();
                else entry.Delete();
            }
        } else if (File.Exists(dir)) {
            File.Delete(dir);
        }
        Directory.CreateDirectory(dir);
    }

    // --offline stops Pi's startup network work (a configured package install runs even with
    // --no-extensions); --no-extensions keeps third-party extensions from starting.
    internal static ProcessStartInfo BuildStartInfo(string piPath, string workingDirectory) {
        var psi = new ProcessStartInfo(piPath, ["--mode", "rpc", "--offline", "--no-extensions", "--no-session"]) {
            WorkingDirectory       = workingDirectory,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        PiLaunchEnvironment.Apply(psi.Environment);
        return psi;
    }

    internal static async Task<IReadOnlyList<VendorModelOption>?> RunAsync(
            string piPath, string workingDirectory,
            Func<ProcessStartInfo, CancellationToken, Task<IPiRpcProcess>> processSource,
            TimeProvider time, ILogger logger, CancellationToken ct) {
        using var deadline = new CancellationTokenSource(Deadline, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        // The deadline covers the spawn too: a stalled start must not hold daemon startup.
        IPiRpcProcess process;
        Task<IPiRpcProcess>? spawn = null;
        try {
            PrepareDirectory(workingDirectory);
            var psi   = BuildStartInfo(piPath, workingDirectory);
            var token = linked.Token;
            spawn     = Task.Run(() => processSource(psi, token), CancellationToken.None);
            process = await spawn.WaitAsync(linked.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested) {
            LogNoResponse(logger, Deadline, "");
            DisposeWhenStarted(spawn);
            return null;
        } catch (OperationCanceledException) {
            DisposeWhenStarted(spawn);
            throw;
        } catch (Exception ex) {
            LogCouldNotStart(logger, ex);
            return null;
        }

        try {
            // Stdin stays open until the response: Pi treats EOF as shutdown without awaiting an
            // in-flight command, so closing early races the answer.
            await process.WriteLineAsync($$"""{"type":"{{Command}}"}""", linked.Token).ConfigureAwait(false);
            await foreach (var line in process.ReadLinesAsync(linked.Token).ConfigureAwait(false)) {
                var parsed = Parse(line);
                if (!parsed.IsResponse) continue;

                await process.CloseInputAsync(ExitGrace).ConfigureAwait(false);
                await process.WaitForExitAsync(ExitGrace).ConfigureAwait(false);
                if (!process.HasExited) await process.TerminateAsync(ExitGrace).ConfigureAwait(false);
                if (parsed.Models is null) LogInvalidEnvelope(logger, Stderr(process));
                return parsed.Models;
            }
            LogExitedBeforeAnswering(logger, process.ExitCode, Stderr(process));
            return null;
        } catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested) {
            LogNoResponse(logger, Deadline, Stderr(process));
            await process.TerminateAsync(ExitGrace).ConfigureAwait(false);
            return null;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogFailed(logger, ex, Stderr(process));
            return null;
        } finally {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    static void DisposeWhenStarted(Task<IPiRpcProcess>? spawn) =>
        spawn?.ContinueWith(t => t.Result.DisposeAsync().AsTask(), CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    static string Stderr(IPiRpcProcess process) =>
        process.Diagnostics is { } d ? d.Length > StderrLogCap ? d[..StderrLogCap] : d : "";

    [LoggerMessage(Level = LogLevel.Warning, Message = "pi model catalog probe could not start")]
    static partial void LogCouldNotStart(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pi model catalog probe: invalid response envelope; stderr: {Stderr}")]
    static partial void LogInvalidEnvelope(ILogger logger, string stderr);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pi model catalog probe: pi exited before answering (exit {ExitCode}); stderr: {Stderr}")]
    static partial void LogExitedBeforeAnswering(ILogger logger, int? exitCode, string stderr);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pi model catalog probe: no response within {Deadline}; stderr: {Stderr}")]
    static partial void LogNoResponse(ILogger logger, TimeSpan deadline, string stderr);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pi model catalog probe failed; stderr: {Stderr}")]
    static partial void LogFailed(ILogger logger, Exception ex, string stderr);

    internal static PiModelCatalogParse Parse(string line) {
        if (string.IsNullOrWhiteSpace(line)) return PiModelCatalogParse.NotResponse;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return PiModelCatalogParse.NotResponse; }
        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject || root.Str("type") != "response" || root.Str("command") != Command)
                return PiModelCatalogParse.NotResponse;
            // An invalid envelope is null, never empty: an empty catalog would suppress every fallback.
            if (root.Bool("success") != true || root.Obj("data")?.Arr("models") is not { } models)
                return new(true, null);

            var list = new List<VendorModelOption>();
            foreach (var m in models.EnumerateArray()) {
                var id = m.Str("id");
                var provider = m.Str("provider");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(provider)) continue;
                var name = m.Str("name") is { Length: > 0 } n ? n : id;
                list.Add(new($"{provider}/{id}", $"{name} · {provider}"));
            }
            return new(true, list);
        }
    }
}
