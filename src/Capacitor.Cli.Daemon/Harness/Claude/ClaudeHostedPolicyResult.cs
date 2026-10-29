namespace Capacitor.Cli.Daemon.Harness.Claude;

using Capacitor.Cli.Core.Policy;

/// <summary>What the session's policy made of one hosted permission request: the outcome the caller
/// acts on, and the event that records it. <see cref="PolicyOutcome.None"/> is a judge that was
/// consulted and did not decide; the request takes the human lane.</summary>
internal sealed record ClaudeHostedPolicyResult(PolicyOutcome Outcome, PolicyDecisionEventV1 Event);
