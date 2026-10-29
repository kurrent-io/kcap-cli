using System.Collections.Frozen;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Points a launch's child at its account's directory. A child that inherits the daemon's own
/// vendor home variable reads that directory, not the one its launcher prepared.</summary>
internal static class VendorHomeEnv {
    /// <summary>Sets the account's variable in <paramref name="overlay"/>, or returns it as the inherited
    /// variable the child must not see. Never touches the daemon's own environment.</summary>
    public static IReadOnlyCollection<string> Apply(
            IDictionary<string, string> overlay, string vendor, ResolvedAccount account, UserHome home) =>
        Apply(overlay, vendor, account.Directory, home);

    /// <inheritdoc cref="Apply(IDictionary{string,string},string,ResolvedAccount,UserHome)"/>
    public static IReadOnlyCollection<string> Apply(
            IDictionary<string, string> overlay, string vendor, string directory, UserHome home) {
        if (HarnessId.From(vendor) is not { } id || VendorHomeVariable.Plan(id, directory, home) is not { } plan)
            return FrozenSet<string>.Empty;

        if (plan.Value is null) {
            overlay.Remove(plan.Variable);

            return [plan.Variable];
        }

        overlay[plan.Variable] = plan.Value;

        return FrozenSet<string>.Empty;
    }
}
