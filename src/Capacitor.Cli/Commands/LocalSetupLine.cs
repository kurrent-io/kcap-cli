using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

/// <summary>One line of <c>kcap setup --json</c> or <c>kcap local upgrade --json</c>. Every event shares this shape and carries only its own
/// fields; the property order is the wire order.</summary>
sealed record LocalSetupLine {
    [JsonPropertyOrder(-2)] public int V { get; init; } = 1;

    [JsonPropertyOrder(-1)] public required string Event { get; init; }

    /// <summary>The setup step a line of <c>kcap setup --json --no-prompt</c> belongs to.</summary>
    public string? Step { get; init; }

    public string? Plan { get; init; }

    public string? Operation { get; init; }

    public string? Phase { get; init; }

    public string? ServerUrl { get; init; }

    public string? InstallationId { get; init; }

    public int? Port { get; init; }

    public string? Version { get; init; }

    public string? InstalledVersion { get; init; }

    public string? FromVersion { get; init; }

    public string? Root { get; init; }

    public string? Url { get; init; }

    public long? Received { get; init; }

    public long? Bytes { get; init; }

    public string? Sha256 { get; init; }

    public string? Path { get; init; }

    public string? Unit { get; init; }

    public string? Reason { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public string? ReplacedModel { get; init; }

    public int? Dimensions { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }

    /// <summary>How the sign-in step authenticated: <c>machine</c>, <c>stored</c>, <c>sign_in</c> or <c>none</c>.</summary>
    public string? Credential { get; init; }

    public string? Visibility { get; init; }

    /// <summary>The vendor ids kcap was installed into.</summary>
    public IReadOnlyList<string>? Harnesses { get; init; }

    public string? Name { get; init; }

    public string? Outcome { get; init; }

    /// <summary>On an upgrade's failure: whether the new version is selected.</summary>
    public bool? Switched { get; init; }
}
