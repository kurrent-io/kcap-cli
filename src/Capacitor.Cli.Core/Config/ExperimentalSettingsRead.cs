using System.Collections.Frozen;

namespace Capacitor.Cli.Core.Config;

/// <param name="Readable">False when the file exists but is empty or cannot be parsed; every feature then reads off.</param>
public sealed record ExperimentalSettingsRead(bool Readable, IReadOnlyDictionary<string, bool> Features, string? Problem) {
    public static ExperimentalSettingsRead Empty { get; } = new(true, FrozenDictionary<string, bool>.Empty, null);

    public bool IsEnabled(string id) => Features.TryGetValue(id, out var on) && on;
}
