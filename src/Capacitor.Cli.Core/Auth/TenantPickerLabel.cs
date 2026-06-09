namespace Capacitor.Cli.Core.Auth;

public static class TenantPickerLabel {
    /// <summary>
    /// Returns a human-readable label for a tenant in the picker:
    ///   - User accounts  → "@login (personal)"
    ///   - Org accounts   → "login"
    /// Falls back to <see cref="DiscoveredTenant.OrgLogin"/> when
    /// <see cref="DiscoveredTenant.AccountLogin"/> is empty (older proxy responses).
    /// </summary>
    public static string Render(DiscoveredTenant tenant) {
        var login = string.IsNullOrEmpty(tenant.AccountLogin) ? tenant.OrgLogin : tenant.AccountLogin;
        return tenant.AccountType == "User"
            ? $"@{login} (personal)"
            : login;
    }
}
