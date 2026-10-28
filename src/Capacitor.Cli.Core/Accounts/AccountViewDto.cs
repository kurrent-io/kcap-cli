using Capacitor.Cli.Core.Accounts.Readings;

namespace Capacitor.Cli.Core.Accounts;

/// <summary><see cref="Vendor"/> is the launch token (<c>claude</c>, <c>codex</c>);
/// <see cref="RecordingState"/> is the recording-state name in snake_case (<c>recording</c>,
/// <c>installed</c>, <c>broken</c>, <c>not_wired</c>). <see cref="Reading"/> is null unless it belongs to
/// <see cref="Generation"/>. Record equality compares <see cref="Reading"/> by reference; compare readings
/// with <see cref="ReadingMerge.Equivalent"/>.</summary>
public sealed record AccountViewDto(
    string             Id,
    string             Vendor,
    string             Directory,
    string             DirectoryKey,
    string             Label,
    string             RecordingState,
    string?            Email           = null,
    string?            OrgId           = null,
    string?            OrgName         = null,
    string?            Plan            = null,
    string?            VendorAccountId = null,
    string?            SignIn          = null,
    long               Generation      = 0,
    DateTimeOffset?    RefreshedAt     = null,
    string?            RefreshError    = null,
    string?            LimitsError     = null,
    AccountReadingDto? Reading         = null
);
