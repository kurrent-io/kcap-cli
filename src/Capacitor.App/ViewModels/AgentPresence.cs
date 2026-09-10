using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

/// The workspace's accumulated view of one agent: its latest dto and whether the session has ended,
/// which stays true once set even if a later dto arrives.
internal sealed record AgentPresence(AgentStatusDto? Dto, bool SessionEnded);
