using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>One vendor config directory kcap records: a Claude <c>CLAUDE_CONFIG_DIR</c> or a Codex
/// <c>CODEX_HOME</c>. <see cref="Directory"/> is normalized and is the account's key.
/// <see cref="WiredAt"/> is the last wiring attempt that succeeded and <see cref="WiringFailure"/> the
/// summary of the latest one that did not; both are null on an entry written before either existed.</summary>
public sealed record VendorAccount(
    [property: JsonPropertyName("id")]             string          Id,
    [property: JsonPropertyName("vendor")]         HarnessId       Vendor,
    [property: JsonPropertyName("directory")]      string          Directory,
    [property: JsonPropertyName("label")]          string          Label,
    [property: JsonPropertyName("added_at")]       DateTimeOffset  AddedAt,
    [property: JsonPropertyName("wired_at")]       DateTimeOffset? WiredAt       = null,
    [property: JsonPropertyName("wiring_failure")] string?         WiringFailure = null);
