namespace Capacitor.Cli.Daemon.Services;

/// <summary>A hosted runtime that can end itself for a coded reason. The orchestrator reads the verdict
/// to report the launch failure, and sends every non-failure agent status through the gated send so a
/// status initiated after the verdict can never clear the failure reason.</summary>
internal interface ITerminationVerdictSource {
    TerminationVerdict? ReadVerdict();

    bool TryInitiateNonFailureStatusSend(Func<Task> send, out Task sendTask);
}
