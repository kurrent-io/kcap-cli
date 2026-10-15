namespace Capacitor.App.ViewModels;

/// One kcap MCP tool as the chat shows it: the bare tool name, the category its rows fold under,
/// the verb phrase the row opens with, the argument that becomes the row's detail, and whether a
/// settled call becomes a card.
public sealed record KcapToolEntry(string Tool, ToolCategory Category, string Label, string? DetailKey, ToolCardKind Card = ToolCardKind.None);
