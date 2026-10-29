using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// Asks an installed `codex app-server` for the models its signed-in account can launch (`model/list`).
/// A ChatGPT-account Codex rejects API-only models the server's catalog also offers, so this is the
/// list a launch can trust. The daemon never reads Codex's auth or cache files.
internal static partial class CodexModelCatalogProbe {
    internal static readonly TimeSpan Deadline  = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    const int MaxPages = 10;

    internal static string DirectoryFor(string stateDir) => Path.Combine(stateDir, "codex-probe");

    /// <summary>One working directory per account, named by a hash of its directory key: the key is a
    /// whole path, so it cannot be a file name itself.</summary>
    internal static string DirectoryFor(string stateDir, string directoryKey) =>
        Path.Combine(DirectoryFor(stateDir),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directoryKey)))[..16].ToLowerInvariant());

    /// <summary>Null on every failure, never an empty catalog: an empty one would suppress the
    /// launcher's other sources.</summary>
    internal static async Task<IReadOnlyList<VendorModelOption>?> RunAsync(
            Func<Task<(CodexAppServerConnection Connection, IAcpProcess Process)>> spawn, string clientVersion,
            TimeProvider time, ILogger logger, CancellationToken ct) {
        using var deadline = new CancellationTokenSource(Deadline, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        CodexAppServerConnection connection;
        IAcpProcess              process;
        Task<(CodexAppServerConnection Connection, IAcpProcess Process)>? started = null;
        try {
            started               = spawn();
            (connection, process) = await started.WaitAsync(linked.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            ShutDownWhenStarted(started, logger);
            if (ct.IsCancellationRequested) throw;

            LogNoResponse(logger, Deadline);
            return null;
        } catch (Exception ex) {
            LogCouldNotStart(logger, ex);
            return null;
        }

        using var stopLoop = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        var loop = connection.RunAsync(stopLoop.Token);
        try {
            var init = new JsonObject {
                ["clientInfo"] = new JsonObject { ["name"] = "kcap-daemon", ["version"] = clientVersion },
            };
            await connection.RequestAsync("initialize", ToElement(init), linked.Token).ConfigureAwait(false);

            // A partial list would override the server's catalog and hide launchable models, so only a
            // complete answer — every page read, the last naming no next cursor — counts.
            var     models = new List<VendorModelOption>();
            string? cursor = null;
            for (var page = 0; page < MaxPages; page++) {
                var @params = new JsonObject { ["cursor"] = cursor };
                var result  = await connection.RequestAsync("model/list", ToElement(@params), linked.Token)
                    .ConfigureAwait(false);
                if (!TryParsePage(result, models, out cursor)) {
                    LogMalformedPage(logger);
                    return null;
                }
                if (cursor is null) return models.Count == 0 ? null : models;
            }

            LogTooManyPages(logger, MaxPages);
            return null;
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            LogNoResponse(logger, Deadline);
            return null;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogFailed(logger, ex);
            return null;
        } finally {
            await ShutDownAsync(connection, process, stopLoop, loop, logger).ConfigureAwait(false);
        }
    }

    /// <summary>A child that starts after the probe stopped waiting is still shut down, never left running.</summary>
    static void ShutDownWhenStarted(Task<(CodexAppServerConnection Connection, IAcpProcess Process)>? started, ILogger logger) =>
        started?.ContinueWith(async t => {
            if (!t.IsCompletedSuccessfully) return;

            var (connection, process) = t.Result;
            using var stopped = new CancellationTokenSource();
            await ShutDownAsync(connection, process, stopped, Task.CompletedTask, logger).ConfigureAwait(false);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Every step runs whatever the one before it did: a broken pipe from a child that already
    /// exited must neither leak the process nor replace the probe's answer.</summary>
    static async Task ShutDownAsync(
            CodexAppServerConnection connection, IAcpProcess process, CancellationTokenSource stopLoop, Task loop, ILogger logger) {
        await Step(connection.CloseInputAsync).ConfigureAwait(false);
        await Step(() => process.WaitForExitAsync(ExitGrace)).ConfigureAwait(false);
        if (!process.HasExited) await Step(() => process.TerminateAsync(ExitGrace)).ConfigureAwait(false);
        await Step(stopLoop.CancelAsync).ConfigureAwait(false);
        await Step(() => loop).ConfigureAwait(false);
        await Step(() => connection.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Step(() => process.DisposeAsync().AsTask()).ConfigureAwait(false);

        async Task Step(Func<Task> step) {
            try {
                await step().ConfigureAwait(false);
            } catch (Exception ex) {
                LogShutdownStepFailed(logger, ex);
            }
        }
    }

    /// <summary>Appends the page's visible models to <paramref name="into"/>; false when the page is not a
    /// model list or its nextCursor is missing or neither null nor a cursor. <paramref name="next"/> is null on the last page.</summary>
    internal static bool TryParsePage(JsonElement result, List<VendorModelOption> into, out string? next) {
        next = null;
        if (result.ValueKind != JsonValueKind.Object
         || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var m in data.EnumerateArray()) {
            if (m.ValueKind != JsonValueKind.Object) continue;
            if (m.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
            if (Str(m, "model") is not { Length: > 0 } model) continue;

            into.Add(new VendorModelOption(model, Str(m, "displayName") is { Length: > 0 } name ? name : model));
        }

        if (!result.TryGetProperty("nextCursor", out var cursor)) return false;
        if (cursor.ValueKind == JsonValueKind.Null) return true;
        if (cursor.ValueKind != JsonValueKind.String || cursor.GetString() is not { Length: > 0 } value) return false;

        next = value;
        return true;
    }

    static JsonElement ToElement(JsonNode node) {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    static string? Str(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "codex model catalog probe could not start")]
    static partial void LogCouldNotStart(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "codex model catalog probe: no response within {Deadline}")]
    static partial void LogNoResponse(ILogger logger, TimeSpan deadline);

    [LoggerMessage(Level = LogLevel.Warning, Message = "codex model catalog probe failed")]
    static partial void LogFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "codex model catalog probe: a model/list page was not a model list")]
    static partial void LogMalformedPage(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "codex model catalog probe: model/list still had pages after {Pages}")]
    static partial void LogTooManyPages(ILogger logger, int pages);

    [LoggerMessage(Level = LogLevel.Debug, Message = "codex model catalog probe: a shutdown step failed")]
    static partial void LogShutdownStepFailed(ILogger logger, Exception ex);
}
