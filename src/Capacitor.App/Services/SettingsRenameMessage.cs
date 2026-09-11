using Capacitor.App.Services.Mutation;

namespace Capacitor.App.Services;

public static class SettingsRenameMessage {
    public static string For(MutationRequest request, MutationOutcome outcome) {
        var reason = outcome switch {
            MutationOutcome.Refused(var token, _) => token,
            MutationOutcome.Failed(var code, var token, _) => token ?? VerifyExitCodes.Token(code),
            MutationOutcome.AttentionSkew(var detail) => detail,
            MutationOutcome.AttentionRepair(var detail) => detail,
            _ => "unconfirmed",
        };
        var explanation = reason switch {
            "cli_unsupported" => "Update the kcap command-line tool before renaming; it cannot retire the old service.",
            "foreign_profile" => "The old service belongs to another profile and was left in place.",
            "unit_unreadable" => "The old service could not be read and was left in place.",
            "verify_contended" => "The daemon name or service is in use.",
            "verify_bootout_unknown" or "verify_stop_unconfirmed" or "verify_rollback_budget" or "verify_restore_verification" =>
                "The service transaction could not confirm that it finished. The old daemon may have stopped.",
            _ => $"The rename was not confirmed ({reason}).",
        };
        return $"Name saved. {explanation} Restart the app to connect using {request.DaemonName}. " +
            $"If the old service remains, remove it with: kcap daemon service uninstall --name {request.RetireServiceId}";
    }
}
