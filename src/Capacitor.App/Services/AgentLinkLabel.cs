namespace Capacitor.App.Services;

/// <param name="AgentId">Normalized, as <see cref="AgentLinkLabels.Titled"/> announces it.</param>
/// <param name="IsTitle">False while the text is the short-id stand-in.</param>
public sealed record AgentLinkLabel(string AgentId, string Text, bool IsTitle);
