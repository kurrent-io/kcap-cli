namespace Capacitor.App.ViewModels;

/// What a producing call left behind, as the chat shows it: the verb line ("Published page"), the
/// thing's own name, one meta line, and whichever target it can open. Url is a page; DocumentPath
/// is a declared document as the declaration spelled it.
public sealed record ToolCard(ToolCardKind Kind, string Title, string Name, string Meta, string? Url, string? DocumentPath);
