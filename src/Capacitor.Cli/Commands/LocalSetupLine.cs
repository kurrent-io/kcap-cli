using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

/// <summary>One line of <c>kcap setup --local --json</c>. Every event shares this shape and carries only its own
/// fields; the property order is the wire order.</summary>
sealed record LocalSetupLine {
    [JsonPropertyOrder(-2)] public int V { get; init; } = 1;

    [JsonPropertyOrder(-1)] public required string Event { get; init; }

    public string? Plan { get; init; }

    public string? Operation { get; init; }

    public string? Phase { get; init; }

    public string? ServerUrl { get; init; }

    public string? InstallationId { get; init; }

    public int? Port { get; init; }

    public string? Version { get; init; }

    public string? InstalledVersion { get; init; }

    public string? Root { get; init; }

    public string? Url { get; init; }

    public long? Received { get; init; }

    public long? Bytes { get; init; }

    public string? Sha256 { get; init; }

    public string? Path { get; init; }

    public string? Unit { get; init; }

    public string? Reason { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }
}
