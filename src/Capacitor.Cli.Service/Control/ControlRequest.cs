namespace Capacitor.Cli.Service.Control;

/// <summary>One line on the pipe. It names a verb and nothing else: whose daemon it acts on comes from the
/// pipe's caller, never from the request</summary>
public sealed record ControlRequest(string Verb);
