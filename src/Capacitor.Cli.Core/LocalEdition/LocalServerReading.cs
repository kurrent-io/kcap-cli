namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>The local server's condition, with what holds its port when the condition is
/// <see cref="LocalServerCondition.PortTaken"/>.</summary>
/// <param name="Holder">The process the OS names as the listener, when it names one.</param>
/// <param name="Answer">What answered on the port, when it answered as another program.</param>
public sealed record LocalServerReading(LocalServerCondition Condition, int? Port = null, PortHolder? Holder = null, string? Answer = null) {
    /// <summary>The holder in the words a sentence can carry: "python3 (pid 4242)", "a web server that calls itself
    /// nginx", or "another program".</summary>
    public string HolderText => Holder is { } holder
        ? $"{holder.Command} (pid {holder.Pid})"
        : Answer ?? "another program";
}
