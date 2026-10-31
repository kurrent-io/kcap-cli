using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>What a connect or a status report tells the server about agents a restart left in flight,
/// read in one step: a participant publishing or failing between separate reads could otherwise be
/// missing from both the live list and the claims while the report says startup is complete.</summary>
internal sealed record RecoverySnapshot(
    string[] LiveIds, LiveAgentInfo[] LiveAgents, ResumingAgentClaim[] Claims, bool StartupReapComplete);
