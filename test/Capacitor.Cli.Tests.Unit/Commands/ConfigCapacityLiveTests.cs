using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ConfigCapacityLiveTests {
    sealed class SettingsOnlyOps(Func<DaemonSettingsPutDto, Task<DaemonSettingsAckDto>> put) : ILocalControlOps {
        public Task<DaemonSettingsAckDto> PutDaemonSettingsAsync(DaemonSettingsPutDto dto, CancellationToken ct) => put(dto);

        public Task<StopAgentResult>  StopAgentAsync(string agentId, bool force, CancellationToken ct)                  => throw new NotSupportedException();
        public Task<ConsentPolicyDto> GetConsentPolicyAsync(CancellationToken ct)                                          => throw new NotSupportedException();
        public Task<ConsentAckDto>    PutConsentPolicyAsync(ConsentPolicyDto policy, CancellationToken ct)                 => throw new NotSupportedException();
        public Task<ConsentAckDto>    PutConsentPolicyV2Async(ConsentPolicyPutV2Dto put, CancellationToken ct)             => throw new NotSupportedException();
        public Task<ConsentAckDto>    ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct)                 => throw new NotSupportedException();
        public Task<PermissionAckDto> ResolvePermissionAsync(PermissionResolveDto resolve, CancellationToken ct)           => throw new NotSupportedException();
        public Task<SendTextResult>   SendTextAsync(string agentId, string text, CancellationToken ct)                      => throw new NotSupportedException();
        public Task<SendTextResult>   SendTextWithAttachmentsAsync(string agentId, string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) =>
            throw new NotSupportedException();        public Task<SessionRenameAckDto> RenameSessionAsync(SessionRenameDto rename, CancellationToken ct)          => throw new NotSupportedException();
        public Task<HandoffProbeReplyDto?> ProbeHandoffAsync(HandoffProbeRequestDto request, CancellationToken ct) => throw new NotSupportedException();
        public Task<HandoffOutcomeDto?> ReadHandoffOutcomeAsync(string sessionId, CancellationToken ct)          => throw new NotSupportedException();
    }

    /// <summary>Pins that the new capacity is handed to the running daemon, and that each answer —
    /// applied, refused, no daemon, a daemon too old to take it live, or no answer — reads as where it
    /// took effect.</summary>
    [Test]
    [Arguments("applied", "Applied to running daemon 'tony'; no restart needed.")]
    [Arguments("refused", "Daemon 'tony' refused the value (invalid_max_agents); it keeps 5.")]
    [Arguments("daemon_unreachable", "Daemon 'tony' is not running; it reads the new value when it starts.")]
    [Arguments("daemon_rejected", "Daemon 'tony' cannot take the value live; it reads it when it restarts")]
    [Arguments("timed_out", "Could not confirm the new value with daemon 'tony' (timed_out)")]
    public async Task Reports_where_the_new_capacity_took_effect(string answer, string expected) {
        DaemonSettingsPutDto? sent = null;

        var ops = new SettingsOnlyOps(dto => {
            sent = dto;

            return answer switch {
                "applied" => Task.FromResult(new DaemonSettingsAckDto(true, null, 0)),
                "refused" => Task.FromResult(new DaemonSettingsAckDto(false, DaemonSettingsReasons.InvalidMaxAgents, 5)),
                _         => Task.FromException<DaemonSettingsAckDto>(new LocalControlOpsException(answer, "x")),
            };
        });

        var line = await ConfigCommand.ApplyCapacityLiveAsync(ops, "tony", 0, CancellationToken.None);

        await Assert.That(sent).IsEqualTo(new DaemonSettingsPutDto(0));
        await Assert.That(line).StartsWith(expected);
        await Assert.That(line.Contains("--max-agents")).IsEqualTo(answer is not ("refused" or "timed_out"));
    }
}
