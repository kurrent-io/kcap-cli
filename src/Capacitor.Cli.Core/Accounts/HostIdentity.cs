using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Accounts;

internal sealed record HostIdentity([property: JsonPropertyName("host_id")] string HostId);
