using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Discards the commit boundary's narration: a local install reports its outcome through its own events, and
/// a failed commit's reason comes back in the result.</summary>
sealed class NoAuthProgress : IAuthProgress {
    public static readonly NoAuthProgress Instance = new();

    public void Notice(string message) { }
    public void Error(string message) { }
    public void BrowserOpening(string url) { }
    public void DeviceCode(string code, string verificationUri, string? provider, bool prefilled) { }
    public void PollTick() { }
}
