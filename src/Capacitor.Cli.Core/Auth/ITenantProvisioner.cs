namespace Capacitor.Cli.Core.Auth;

public enum ProvisionOfferStatus { Created, Declined, InProgress, Failed }

public sealed record ProvisionedTenant(
    string OrganizationId, string Slug, string DisplayName, string Origin);

// Result of offering to create a tenant. The provisioner OWNS all user-facing
// messaging for Declined/InProgress/Failed; the caller must not print a second,
// conflicting message (e.g. the legacy "ask your admin" dead-end).
public sealed record ProvisionOffer(ProvisionOfferStatus Status, ProvisionedTenant? Tenant) {
    public static ProvisionOffer Created(ProvisionedTenant t) => new(ProvisionOfferStatus.Created, t);
    public static readonly ProvisionOffer Declined   = new(ProvisionOfferStatus.Declined,   null);
    public static readonly ProvisionOffer InProgress = new(ProvisionOfferStatus.InProgress, null);
    public static readonly ProvisionOffer Failed     = new(ProvisionOfferStatus.Failed,     null);
}

public interface ITenantProvisioner {
    // Interactive: prompt -> provision -> poll. Returns Created (with the tenant)
    // on success; Declined/InProgress/Failed otherwise.
    Task<ProvisionOffer> OfferCreateAsync(string workosAccessToken, CancellationToken ct = default);
}
