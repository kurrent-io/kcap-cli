using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Recording;
using Capacitor.Cli.Core.Resume;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal partial class AgentOrchestrator {
    internal HarnessRegistry HarnessesForTest => _harnesses;

    /// <summary>Answers whether an ended session can be resumed here. A payload that does not parse is refused
    /// <c>resume_invalid</c> rather than answered with an Error frame.</summary>
    public async Task HandleLocalResumeProbeAsync(string payload, Stream stream, CancellationToken ct) {
        string? refusal;
        try {
            refusal = JsonSerializer.Deserialize(payload, ResumeInPlaceIpcJsonContext.Default.ResumeProbeRequestDto) is { } request
                ? await InPlaceResumeRefusalAsync(request.Vendor, request.SessionId, request.Directory, request.AccountId, request.ConfigDir)
                : ResumeInPlaceRefusals.Invalid;
        } catch (JsonException) {
            refusal = ResumeInPlaceRefusals.Invalid;
        }

        try {
            var json = JsonSerializer.Serialize(new ResumeProbeReplyDto(refusal), ResumeInPlaceIpcJsonContext.Default.ResumeProbeReplyDto);
            await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.ResumeProbeReply) { Text = json }, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogDebug(ex, "ResumeProbe: the reply could not be written");
        }
    }

    /// <summary>Why the session cannot be resumed natively in <paramref name="directory"/> under the store it was
    /// recorded with, or null when it can. The probe and the launch run this same check.</summary>
    internal async Task<string?> InPlaceResumeRefusalAsync(string vendor, string sessionId, string directory, string? accountId, string? configDir) {
        if (NativeResumeId.For(vendor, sessionId) is null
         || !_runtimeFactories.TryGetValue(vendor.ToLowerInvariant(), out var factory)
         || !factory.SupportsRestartResume(null))
            return ResumeInPlaceRefusals.UnsupportedVendor;

        if (SessionId.Parse(SessionIds.Canonical(sessionId)) is { } session && HostsSession(session)) return ResumeInPlaceRefusals.SourceActive;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return ResumeInPlaceRefusals.DirectoryMissing;

        if (!(await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(directory)).Allowed) return ResumeInPlaceRefusals.DirectoryNotAllowed;

        return StoreRefusal(vendor, accountId, configDir);
    }

    /// <summary>The recorded store must be the one a resume runs under. An account vendor resolves it the way restart
    /// recovery does, keeping the recorded directory; with no registered account it must still be today's default,
    /// so a changed default is not resumed into. A vendor without accounts must still resolve its root to it.</summary>
    string? StoreRefusal(string vendorToken, string? accountId, string? configDir) {
        if (string.IsNullOrWhiteSpace(configDir) || HarnessId.From(vendorToken) is not { } vendor) return ResumeInPlaceRefusals.StoreChanged;

        try {
            if (!_config.DefaultDirectories.TryGetValue(vendor, out var defaultDirectory))
                return RecordingStore.Of(vendor, null, _harnesses) is { } root && AccountDirectory.Same(root, configDir) && Directory.Exists(configDir)
                    ? null
                    : ResumeInPlaceRefusals.StoreChanged;

            var resolved = AccountResolution.ResolveForResume(_config.Accounts?.TryLoad(), vendor, accountId, configDir, defaultDirectory);

            if (resolved.Account is not { } account || account.Directory != configDir) return ResumeInPlaceRefusals.StoreChanged;

            return account.AccountId is not null || AccountDirectory.Same(defaultDirectory, configDir) ? null : ResumeInPlaceRefusals.StoreChanged;
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) {
            return ResumeInPlaceRefusals.StoreChanged;
        }
    }
}
