namespace Capacitor.Cli.Core.LocalEdition;

public sealed class LocalBundleException(LocalInstallFailureCode code, string message, Exception? inner = null)
    : Exception(message, inner) {
    public LocalInstallFailureCode Code { get; } = code;
}
