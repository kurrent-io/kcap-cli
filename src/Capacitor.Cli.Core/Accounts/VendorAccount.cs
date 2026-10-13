using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>One vendor config directory kcap records: a Claude <c>CLAUDE_CONFIG_DIR</c> or a Codex
/// <c>CODEX_HOME</c>. <see cref="Directory"/> is normalized and is the account's key.</summary>
public sealed record VendorAccount(
    [property: JsonPropertyName("id")]        string         Id,
    [property: JsonPropertyName("vendor")]    HarnessId      Vendor,
    [property: JsonPropertyName("directory")] string         Directory,
    [property: JsonPropertyName("label")]     string         Label,
    [property: JsonPropertyName("added_at")]  DateTimeOffset AddedAt);
