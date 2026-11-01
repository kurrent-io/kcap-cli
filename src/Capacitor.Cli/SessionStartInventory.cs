using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Recording;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli;

/// <summary>
/// Stamps this machine's harness inventory, platform and the session's recording identity onto a
/// SessionStart body, so a daemonless machine still reports them through the hook carrier. The inventory is
/// serialized through the same <see cref="CapacitorJsonContext"/> as the daemon's copy, so both carriers are
/// byte-identical. Never throws — a probe failure just omits the field (must never break a hook).
/// </summary>
static class SessionStartInventory {
    public static void Stamp(
            JsonObject body, ConfigRoot config, HarnessRegistry harnesses, HarnessId vendor, TimeProvider time, string? transcriptPath = null,
            AccountStore? accounts = null
        ) {
        try {
            var inv  = HarnessInventory.EvaluateCurrent(config, harnesses, time);
            var json = JsonSerializer.Serialize(inv, CapacitorJsonContext.Default.HarnessInventory);
            body["harness_inventory"] = JsonNode.Parse(json);
        } catch {
            // best-effort metadata — never break a hook
        }
        // The CLI's own OS, feeding the server's live applicability gate. Independent of the
        // inventory probe (its own best-effort boundary — an inventory failure must not cost the
        // platform axis), and deliberately no path/heuristic inference: omitted when unrecognized,
        // and unknown EXCLUDES platform-restricted facts server-side, which beats a wrong guess.
        try {
            if (HostPlatform.Normalized is { } platform) body["platform"] = platform;
        } catch {
            // best-effort metadata — never break a hook
        }

        StampRecording(body, config, harnesses, vendor, transcriptPath, accounts);
    }

    /// <param name="transcriptPath">The vendor's transcript when the body does not carry it as <c>transcript_path</c>:
    /// without it, an ACP vendor's root is accepted unchecked.</param>
    public static void StampRecording(
            JsonObject body, ConfigRoot config, HarnessRegistry harnesses, HarnessId vendor, string? transcriptPath = null, AccountStore? accounts = null
        ) {
        try {
            body[RecordingIdentity.JsonName] = RecordingOf(body, config, harnesses, vendor, transcriptPath, accounts, Environment.GetEnvironmentVariable).ToJson();
        } catch {
            // best-effort metadata — never break a hook
        }
    }

    internal static RecordingIdentity RecordingOf(
            JsonObject body, ConfigRoot config, HarnessRegistry harnesses, HarnessId vendor, string? transcriptPath, AccountStore? accounts,
            Func<string, string?> env
        ) {
        var transcript = transcriptPath ?? (body["transcript_path"] is JsonValue v && v.TryGetValue<string>(out var path) ? path : null);
        var store      = RecordingStore.Of(vendor, transcript, harnesses);
        var account    = store is null ? null : RecordingStore.AccountFor(vendor, store, accounts?.TryLoad());

        return new RecordingIdentity(MachineIdOrNull(config), DaemonName(env), account, store);
    }

    // A config directory nothing may write leaves the id unminted; the rest of the identity still stands.
    static string? MachineIdOrNull(ConfigRoot config) {
        try {
            return new MachineId(config).Get();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    // A terminal may export the daemon's name for its own use; only the pair a daemon launch sets marks a
    // session the daemon hosts.
    static string? DaemonName(Func<string, string?> env) =>
        env(HostedAgent.DaemonAgentIdVar) is { Length: > 0 } && env(DaemonNameResolver.NameVar) is { Length: > 0 } name ? name : null;
}
