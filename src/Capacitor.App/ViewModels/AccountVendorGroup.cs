namespace Capacitor.App.ViewModels;

public sealed record AccountVendorGroup(string Vendor, string VendorLabel, IReadOnlyList<AccountSettingsRow> Rows) {
    /// The last action's problem, shown in this vendor's card when that action was on one of its accounts.
    public AccountsNotice? Notice { get; init; }
}
