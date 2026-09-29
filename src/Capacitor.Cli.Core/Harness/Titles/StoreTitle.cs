using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Harness.Titles;

/// <summary>A title read from a harness's own store, with what the store says about it.</summary>
public sealed record StoreTitle(string Title, HarnessTitleKind Kind, DateTimeOffset? RecordedChangeAt);
