namespace Capacitor.Cli.LocalEdition;

/// <summary>Who answers <c>/auth/config</c> on the installation's port.</summary>
enum AuthConfigAnswer {
    /// <summary>The provider-less local server with its fence lifted.</summary>
    Lifted,

    /// <summary>The same server, still fenced as a pending installation.</summary>
    Pending,

    /// <summary>Anything else, including a failed or refused request.</summary>
    Other
}
