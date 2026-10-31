using Capacitor.App.Services.Mutation;

namespace Capacitor.App.Services;

/// <summary>
/// The rename refusals the CLI reports before it changes either service, so the old daemon still runs
/// under its old name: the saved name must be restored and the old id is not retired.
/// </summary>
public static class RenameRefusal {
    public static bool ChangedNothing(MutationOutcome? outcome) =>
        outcome is MutationOutcome.Failed { Reason: "target_occupied" or "target_unknown" or "agents_active" or "fence_unsupported" or "fence_unavailable" };

    public static string Message(string reason, string newName) => reason switch {
        "target_occupied"   => $"A service named {newName} is already installed on this machine, possibly stopped. Choose another name. Nothing was changed.",
        "target_unknown"    => $"Could not tell whether a service named {newName} is installed. Nothing was changed. Try again.",
        "agents_active"     => "An agent or evaluation started on this daemon before it could be renamed. Wait for it to finish, then try again. Nothing was changed.",
        "fence_unsupported" => "The daemon runs an older version of kcap that cannot be renamed. Update kcap. Once the daemon has restarted, try again. Nothing was changed.",
        _                   => "Could not reach the running daemon to pause new work for the rename. Nothing was changed. Try again in a couple of minutes.",
    };
}
