namespace Capacitor.Cli.Commands;

/// <summary>What this CLI authenticates as.</summary>
public enum StatusAuthState {
    /// <summary>The server announces no auth provider, so every request goes out as it stands and
    /// there is no credential to hold.</summary>
    NotRequired,

    /// <summary>Both machine variables set, so this CLI records as the machine.</summary>
    Machine,

    /// <summary>One machine variable set, which diverts auth off the token store and then fails:
    /// nothing records, and <c>login</c> is not the fix.</summary>
    MachineIncomplete,

    /// <summary>A stored token, bound to the configured server.</summary>
    Valid,

    /// <summary>A token issued by a different server, withheld before any request.</summary>
    WrongServer,

    Expired,

    None,
}

public static class StatusAuthStates {
    extension(StatusAuthState state) {
        /// <summary>The spelling in the <c>status --json</c> payload. Whatever parses that payload
        /// matches on these, so each one is a compatibility constraint.</summary>
        public string Wire => state switch {
            StatusAuthState.NotRequired       => "not_required",
            StatusAuthState.Machine           => "machine",
            StatusAuthState.MachineIncomplete => "machine_incomplete",
            StatusAuthState.Valid             => "valid",
            StatusAuthState.WrongServer       => "wrong_server",
            StatusAuthState.Expired           => "expired",
            StatusAuthState.None              => "none",
        };
    }
}
