using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>Who a vendor says is signed in to an account's directory.</summary>
public sealed record AccountIdentity(string? Email, string? OrgId, string? OrgName, string? Plan, string? VendorAccountId) {
    public static AccountIdentity Of(VendorAccount account) =>
        new(account.Email, account.OrgId, account.OrgName, account.Plan, account.VendorAccountId);

    /// <summary>Whether <paramref name="probed"/> is another login than <paramref name="recorded"/>. A field
    /// missing on either side is not evidence of another login, and nothing differs from an account never
    /// probed. Codex may answer without an email, so its account id decides whenever both sides carry one.</summary>
    public static bool Differs(HarnessId vendor, AccountIdentity recorded, AccountIdentity probed) {
        if (recorded is { Email: null, VendorAccountId: null }) return false;

        if (vendor is HarnessId.Codex)
            return recorded.VendorAccountId is not null && probed.VendorAccountId is not null
                ? recorded.VendorAccountId != probed.VendorAccountId
                : Conflict(recorded.Email, probed.Email);

        return Conflict(recorded.OrgId, probed.OrgId) || Conflict(recorded.Email, probed.Email);
    }

    static bool Conflict(string? recorded, string? probed) => recorded is not null && probed is not null && recorded != probed;

    /// <summary>The identity to record. A field the probe left null keeps the recorded value, unless the login
    /// changed: then it is cleared, so a new login is never recorded beside the previous one's id or email.</summary>
    public static AccountIdentity Commit(AccountIdentity recorded, AccountIdentity probed, bool differs) =>
        differs
            ? probed
            : new(probed.Email ?? recorded.Email, probed.OrgId ?? recorded.OrgId, probed.OrgName ?? recorded.OrgName,
                  probed.Plan ?? recorded.Plan, probed.VendorAccountId ?? recorded.VendorAccountId);
}
