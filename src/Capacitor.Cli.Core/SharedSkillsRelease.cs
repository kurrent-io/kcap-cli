namespace Capacitor.Cli.Core;

/// <summary>What <see cref="SharedSkillsConsumers.Release"/> did: the consumers still relying on the
/// shared skills, and the removal it ran once none were left.</summary>
public sealed record SharedSkillsRelease(IReadOnlyList<string> Remaining, RemovalResult Removal);
