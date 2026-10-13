namespace Capacitor.Cli;

/// <summary>One piece of SessionStart context. Fragments with a lower <paramref name="Rank"/> get room
/// first; <paramref name="Trimmable"/> marks one-item-per-line text that may be cut at a line break
/// to fit, where any other fragment is kept whole or dropped.</summary>
readonly record struct ContextFragment(string? Text, int Rank = 0, bool Trimmable = false);
