namespace Capacitor.Cli.Core.LocalEdition;

public enum LocalModelDownloadCode {
    /// <summary>The publication has no such file.</summary>
    ModelUnavailable,
    /// <summary>The network failed or the download timed out.</summary>
    DownloadFailed,
    /// <summary>The bytes are not the ones the manifest signed.</summary>
    VerificationFailed
}
