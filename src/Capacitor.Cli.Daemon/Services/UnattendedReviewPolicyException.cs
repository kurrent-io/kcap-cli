namespace Capacitor.Cli.Daemon.Services;

/// <summary>A hard reviewer-safety rejection that runtime startup must never soften.</summary>
public sealed class UnattendedReviewPolicyException(string message) : InvalidOperationException(message);
