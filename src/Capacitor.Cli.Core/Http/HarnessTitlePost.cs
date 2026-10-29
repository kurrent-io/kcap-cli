namespace Capacitor.Cli.Core.Http;

/// <summary><paramref name="ChangedAt"/> is null when the harness's own store does not record when
/// the title changed.</summary>
public sealed record HarnessTitlePost(string Title, HarnessTitleKind Kind, DateTimeOffset? ChangedAt);
