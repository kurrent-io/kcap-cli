using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>An upgrade that did not finish. Without <see cref="Switched"/> the old version is still selected; with it
/// the new one is, and is left stopped or, when the switch itself could not be confirmed, journaled for the next
/// lifecycle command to finish.</summary>
sealed class LocalUpgradeFailure(LocalInstallFailureCode code, string message, bool switched) : Exception(message) {
    public LocalInstallFailureCode Code { get; } = code;

    public bool Switched { get; } = switched;
}
