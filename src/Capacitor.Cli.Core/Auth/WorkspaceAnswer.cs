namespace Capacitor.Cli.Core.Auth;

/// <summary>What a workspace's address said when asked for its <c>/auth/config</c>.</summary>
public enum WorkspaceAnswer {
    Live,

    /// <summary>Something answered, and it is not a workspace: a removed tenant's host 404s at the ingress.</summary>
    Gone,

    /// <summary>No answer, or one that says nothing about whether the workspace exists.</summary>
    NoAnswer,

    /// <summary>
    /// Still being created and not live yet. Left out like <see cref="Gone"/>, but discovery then takes no
    /// other workspace without asking: the one the user is waiting for may be this one.
    /// </summary>
    Pending
}
