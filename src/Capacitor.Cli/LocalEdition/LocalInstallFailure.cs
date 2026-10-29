using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>A local install refused or failed. The message is for the user; <see cref="Code"/> is what events carry.</summary>
sealed class LocalInstallFailure(LocalInstallFailureCode code, string message) : Exception(message) {
    public LocalInstallFailureCode Code { get; } = code;
}
