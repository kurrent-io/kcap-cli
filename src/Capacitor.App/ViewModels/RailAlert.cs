namespace Capacitor.App.ViewModels;

/// What the rail footer says when something needs attention. A healthy stack has no alert at all.
/// <param name="Tone">A <see cref="StatusColors"/> value.</param>
/// <param name="Detail">The hover: the longer explanation the one-line text leaves out.</param>
public sealed record RailAlert(string Text, string Tone, RailRecovery Recovery = RailRecovery.None, string? Detail = null) {
    public string? ActionLabel => Recovery switch {
        RailRecovery.SignIn           => "Sign in",
        RailRecovery.StartDaemon      => "Start daemon",
        RailRecovery.Reconnect        => "Reconnect",
        RailRecovery.StartLocalServer => "Start server",
        _                             => null,
    };
}
