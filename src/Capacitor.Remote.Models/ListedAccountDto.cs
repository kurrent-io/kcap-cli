using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One vendor account registered on a daemon's host. A server older than the identity and reading
/// fields omits them, so they bind null and a zero generation.
public sealed record ListedAccountDto {
    [JsonPropertyName("id")]                public required string Id { get; init; }
    [JsonPropertyName("vendor")]            public required string Vendor { get; init; }
    [JsonPropertyName("directory")]         public required string Directory { get; init; }
    [JsonPropertyName("directory_key")]     public required string DirectoryKey { get; init; }
    [JsonPropertyName("label")]             public required string Label { get; init; }
    [JsonPropertyName("recording_state")]   public required string RecordingState { get; init; }
    [JsonPropertyName("email")]             public string? Email { get; init; }
    [JsonPropertyName("org_id")]            public string? OrgId { get; init; }
    [JsonPropertyName("org_name")]          public string? OrgName { get; init; }
    [JsonPropertyName("plan")]              public string? Plan { get; init; }
    [JsonPropertyName("vendor_account_id")] public string? VendorAccountId { get; init; }
    [JsonPropertyName("sign_in")]           public string? SignIn { get; init; }
    [JsonPropertyName("generation")]        public long Generation { get; init; }
    [JsonPropertyName("refreshed_at")]      public DateTimeOffset? RefreshedAt { get; init; }
    [JsonPropertyName("refresh_error")]     public string? RefreshError { get; init; }
    [JsonPropertyName("limits_error")]      public string? LimitsError { get; init; }
    [JsonPropertyName("reading")]           public ListedReadingDto? Reading { get; init; }
}
