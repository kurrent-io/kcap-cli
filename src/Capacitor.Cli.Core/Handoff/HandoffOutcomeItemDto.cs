namespace Capacitor.Cli.Core.Handoff;

/// <summary>One thing a hand-off tried to carry. Kind is work_item, plan, claims or plans; Result is
/// carried, refused, skipped, failed or unknown.</summary>
public sealed record HandoffOutcomeItemDto(string Kind, string Label, string Result, string? Detail);
