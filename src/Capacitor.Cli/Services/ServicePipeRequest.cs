namespace Capacitor.Cli.Services;

/// <summary>One line to <c>kcap-service</c>'s pipe. It names a verb only: the service acts on the caller's
/// own daemon, identified from the pipe connection</summary>
sealed record ServicePipeRequest(string Verb);
