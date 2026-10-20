using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary>The <c>code</c> of a <c>failed</c> line that <c>kcap setup --json --no-prompt</c> writes outside the local
/// install, whose own codes are <see cref="LocalInstallFailureCode"/>'s. Fixed once shipped: a wrapper branches on them.</summary>
static class SetupFailureCodes {
    public static readonly string AnswerRequired   = LocalInstallFailureCode.AnswerRequired.Wire();
    public static readonly string InvalidArguments = LocalInstallFailureCode.InvalidArguments.Wire();
    public static readonly string Cancelled        = LocalInstallFailureCode.Cancelled.Wire();

    public const string ServerUnreachable         = "server_unreachable";
    public const string WorkspaceUnavailable      = "workspace_unavailable";
    public const string SignInFailed              = "sign_in_failed";
    public const string MachineCredentialRejected = "machine_credential_rejected";
    public const string DaemonNameHeld            = "daemon_name_held";

    /// <summary>A failure no more specific code names; the message is the last thing setup wrote to stderr.</summary>
    public const string SetupFailed = "setup_failed";
}
