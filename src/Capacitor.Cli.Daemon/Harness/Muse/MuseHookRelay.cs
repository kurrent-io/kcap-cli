using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>
/// Runs <c>kcap hook --muse</c> on a hosted session's behalf when the daemon host has no kcap hooks in
/// Muse's settings, so the session is recorded on the same path a local one is. <c>muse serve</c>
/// ignores per-launch hooks, which is why the daemon has to stand in.
/// </summary>
internal static class MuseHookRelay {
    static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(20);

    /// <summary>The hook can leave a detached child holding its output pipes open after it exits, so the
    /// drain is bounded separately from the exit.</summary>
    internal static readonly TimeSpan DrainCeiling = TimeSpan.FromSeconds(3);

    /// <summary>Never throws: a relay that fails leaves the session unrecorded, not the launch failed.</summary>
    internal static async Task RelayAsync(
            string            capacitorPath,
            JsonObject        payload,
            string            sessionId,
            string            agentId,
            string?           serverUrl,
            string            daemonId,
            string            daemonEpoch,
            ILogger           logger,
            CancellationToken ct) {
        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var psi = new ProcessStartInfo(capacitorPath, ["hook", "--muse", "--no-update-check"]) {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardInputEncoding  = noBomUtf8,
            StandardOutputEncoding = noBomUtf8,
            StandardErrorEncoding  = noBomUtf8,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        // The hook reads the parent link from these, exactly as Muse sets them for a top-level session.
        psi.Environment["MUSE_SESSION_ID"]     = sessionId;
        psi.Environment["MUSE_THREAD_ID"]      = sessionId;
        psi.Environment[HostedAgent.AgentIdVar] = agentId;
        if (!string.IsNullOrEmpty(serverUrl))   psi.Environment[ProfileOverrides.UrlVar] = serverUrl;
        if (!string.IsNullOrEmpty(daemonId))    psi.Environment["KCAP_DAEMON_ID"]        = daemonId;
        if (!string.IsNullOrEmpty(daemonEpoch)) psi.Environment["KCAP_DAEMON_EPOCH"]     = daemonEpoch;

        var hookEvent = payload["hook_event_name"] is JsonValue v && v.TryGetValue<string>(out var e) ? e : "?";
        Process? process = null;

        try {
            process = Process.Start(WindowsLaunchCommand.Apply(psi, BinaryProbe.FromEnvironment()));
            if (process is null) {
                logger.LogWarning("Muse hook relay: kcap did not start for {Event} (agentId={AgentId}).", hookEvent, agentId);
                return;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

            await process.StandardInput.WriteAsync(payload.ToJsonString()).ConfigureAwait(false);
            process.StandardInput.Close();

            using var ceiling = new CancellationTokenSource(Ceiling, TimeProvider.System);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, ceiling.Token);

            try {
                await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                ProcessTree.Kill(process);

                if (ct.IsCancellationRequested)
                    logger.LogInformation("Muse hook relay: {Event} was cancelled with its launch (agentId={AgentId}).", hookEvent, agentId);
                else
                    logger.LogWarning("Muse hook relay: {Event} did not finish within {Seconds}s (agentId={AgentId}).",
                        hookEvent, Ceiling.TotalSeconds, agentId);
                return;
            }

            try {
                await Task.WhenAll(stdout, stderr).WaitAsync(DrainCeiling, TimeProvider.System, ct).ConfigureAwait(false);
            } catch (TimeoutException) {
                logger.LogDebug("Muse hook relay: {Event} output was still open {Seconds}s after exit (agentId={AgentId}).",
                    hookEvent, DrainCeiling.TotalSeconds, agentId);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            }

            if (process.ExitCode != 0)
                logger.LogWarning("Muse hook relay: {Event} exited {ExitCode} (agentId={AgentId}): {Stderr}",
                    hookEvent, process.ExitCode, agentId, stderr.IsCompletedSuccessfully ? stderr.Result : "");
        } catch (Exception ex) {
            logger.LogWarning(ex, "Muse hook relay: {Event} failed (agentId={AgentId}).", hookEvent, agentId);
        } finally {
            process?.Dispose();
        }
    }
}
