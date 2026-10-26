namespace Capacitor.App.ViewModels;

public sealed record AccountVendorGroup(string Vendor, string VendorLabel, IReadOnlyList<AccountSettingsRow> Rows);
