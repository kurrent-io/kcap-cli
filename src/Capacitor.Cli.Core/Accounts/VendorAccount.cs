using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>One vendor config directory kcap records: a Claude <c>CLAUDE_CONFIG_DIR</c> or a Codex
/// <c>CODEX_HOME</c>. <see cref="Directory"/> is normalized and is the account's key.
/// <see cref="WiredAt"/> is the last wiring attempt that succeeded and <see cref="WiringFailure"/> the
/// summary of the latest one that did not. <see cref="Generation"/> starts at 0 for an entry written without it;
/// <see cref="GenerationStartedAt"/> is when the probe that detected the current generation's login began.
/// <see cref="RetiredSessions"/> is read only by older builds' status-line writers, which refuse a listed session's
/// replay; this build records into the current generation regardless.</summary>
public sealed record VendorAccount(
    [property: JsonPropertyName("id")]                     string            Id,
    [property: JsonPropertyName("vendor")]                 HarnessId         Vendor,
    [property: JsonPropertyName("directory")]              string            Directory,
    [property: JsonPropertyName("label")]                  string            Label,
    [property: JsonPropertyName("added_at")]               DateTimeOffset    AddedAt,
    [property: JsonPropertyName("wired_at")]               DateTimeOffset?   WiredAt              = null,
    [property: JsonPropertyName("wiring_failure")]         string?           WiringFailure        = null,
    [property: JsonPropertyName("generation")]             long              Generation           = 0,
    [property: JsonPropertyName("retired_sessions")]       string[]?         RetiredSessions      = null,
    [property: JsonPropertyName("status_line")]            StatusLineBackup? StatusLine           = null,
    [property: JsonPropertyName("email")]                  string?           Email                = null,
    [property: JsonPropertyName("org_id")]                 string?           OrgId                = null,
    [property: JsonPropertyName("org_name")]               string?           OrgName              = null,
    [property: JsonPropertyName("plan")]                   string?           Plan                 = null,
    [property: JsonPropertyName("vendor_account_id")]      string?           VendorAccountId      = null,
    [property: JsonPropertyName("sign_in")]                string?           SignIn               = null,
    [property: JsonPropertyName("probe_seq")]              long              ProbeSeq             = 0,
    [property: JsonPropertyName("committed_probe_seq")]    long              CommittedProbeSeq    = 0,
    [property: JsonPropertyName("refreshed_at")]           DateTimeOffset?   RefreshedAt          = null,
    [property: JsonPropertyName("refresh_error")]          string?           RefreshError         = null,
    [property: JsonPropertyName("limits_error")]           string?           LimitsError          = null,
    [property: JsonPropertyName("hook_trust")]             string?           HookTrust            = null,
    [property: JsonPropertyName("hook_trust_fingerprint")] string?           HookTrustFingerprint = null,
    [property: JsonPropertyName("generation_started_at")]  DateTimeOffset?   GenerationStartedAt  = null,
    [property: JsonPropertyName("probe_started_at")]       DateTimeOffset?   ProbeStartedAt       = null,
    [property: JsonPropertyName("label_set_by_user")]      bool?             LabelSetByUser       = null
) {
    /// <summary>An entry that never recorded the flag kept the directory name <c>kcap accounts add</c> gave it
    /// unless somebody renamed it, so only that default may be replaced by a refreshed email.</summary>
    [JsonIgnore]
    public bool LabelIsUserSet => LabelSetByUser ?? Label != Path.GetFileName(Directory);

    /// <summary>True once the login changed at least once. An entry from before the timestamp existed has a
    /// nonzero generation and no <see cref="GenerationStartedAt"/>.</summary>
    [JsonIgnore]
    public bool HasSwitched => Generation > 0 || GenerationStartedAt is not null;

    /// <summary>Observations produced before this are from an earlier login. Null before the first switch. A
    /// switch recorded without <see cref="GenerationStartedAt"/> falls back to the last refresh, or registration
    /// when there was none: no earlier than the switch, so a backlog from before it never passes.</summary>
    [JsonIgnore]
    public DateTimeOffset? ObservationsFrom =>
        GenerationStartedAt ?? (Generation > 0 ? RefreshedAt ?? AddedAt : null);
}
