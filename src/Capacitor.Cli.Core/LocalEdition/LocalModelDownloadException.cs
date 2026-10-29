namespace Capacitor.Cli.Core.LocalEdition;

public sealed class LocalModelDownloadException(LocalModelDownloadCode code, string message, Exception? inner = null)
    : Exception(message, inner) {
    public LocalModelDownloadCode Code { get; } = code;
}
