using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>The status line kcap displaced when it wired an account. A null <see cref="Original"/> is a
/// backup of "none", distinct from the account carrying no backup at all. Never published.</summary>
public sealed record StatusLineBackup([property: JsonPropertyName("original")] string? Original);
