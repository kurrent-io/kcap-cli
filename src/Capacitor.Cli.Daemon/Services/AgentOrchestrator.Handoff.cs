using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal partial class AgentOrchestrator {
    /// <summary>Resolves a hand-off's starting code for the app. Every outcome is one reply; a payload
    /// that does not parse gets an <c>invalid_target</c> refusal rather than an Error frame.</summary>
    public async Task HandleLocalHandoffProbeAsync(string payload, Stream stream, CancellationToken ct) {
        HandoffProbeReplyDto reply;
        try {
            var request = JsonSerializer.Deserialize(payload, HandoffIpcJsonContext.Default.HandoffProbeRequestDto);
            reply = request is null ? Refused(HandoffRefusals.InvalidTarget) : await ProbeHandoffAsync(request, ct);
        } catch (JsonException) {
            reply = Refused(HandoffRefusals.InvalidTarget);
        }

        try {
            var json = JsonSerializer.Serialize(reply, HandoffIpcJsonContext.Default.HandoffProbeReplyDto);
            await FrameCodec.WriteAsync(stream, LocalFrame.HandoffJson(FrameType.HandoffProbeReply, json), ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogDebug(ex, "HandoffProbe: the reply could not be written");
        }
    }

    /// <summary>The allow-list and the origin identity are decided before anything is fetched, so a path
    /// or a checkout the daemon has no business reading never reaches the network.</summary>
    internal async Task<HandoffProbeReplyDto> ProbeHandoffAsync(HandoffProbeRequestDto request, CancellationToken ct) {
        var authorized = await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(request.RepoPath);
        if (!authorized.Allowed) return Refused(HandoffRefusals.RepoNotAllowed);

        if (authorized.CanonicalGitRoot is not { } root || !OriginIs(root, request.RepoOwner, request.RepoName))
            return Refused(HandoffRefusals.RepoMismatch);

        try {
            var target     = new HandoffTarget(request.Branch, request.PullRequestNumber, request.HeadIsFork);
            var resolution = await _worktreeManager.ResolveHandoffStartAsync(root, target, ct);
            if (resolution.Start is not { } start)
                return new(null, resolution.FetchError ?? "origin could not be read", null, null, null);

            var work = await _worktreeManager.CheckHandoffWorkAsync(root, start.Sha, request.SessionCommits, mayHaveUnpushedWork: true);
            var launch = new HandoffLaunchStart(
                "", start.Sha, start.Checkout.ToString(), start.Branch, start.LocalBranchSha, start.Source.ToString(),
                start.DetachReason.ToString(), request.PullRequestNumber, request.HeadIsFork, request.HeadOwner, request.HeadRepo);

            return new(null, null, launch, work.Completeness.ToString(), work.MissingCommits);
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
            _logger.LogDebug(ex, "HandoffProbe: the target was refused");

            return Refused(HandoffRefusals.InvalidTarget);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogWarning(ex, "HandoffProbe: reading the checkout failed");

            return new(null, ex.Message, null, null, null);
        }
    }

    static bool OriginIs(string gitRoot, string owner, string name) {
        var (originOwner, originName) = GitUrlParser.ParseRemoteUrl(
            GitRemoteReader.ReadOriginUrl(GitRepository.ResolveMainRepoRoot(gitRoot)));

        return string.Equals(originOwner, owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originName, name, StringComparison.OrdinalIgnoreCase);
    }

    static HandoffProbeReplyDto Refused(string refusal) => new(refusal, null, null, null, null);
}
