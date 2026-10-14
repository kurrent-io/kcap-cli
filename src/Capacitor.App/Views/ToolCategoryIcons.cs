using Capacitor.App.ViewModels;
using Lucide.Avalonia;

namespace Capacitor.App.Views;

/// Semantic category mappings shared by chat headers and pending prompts.
public static class ToolCategoryIcons {
    public static LucideIconKind ForCategory(ToolCategory category) => category switch {
        ToolCategory.Read      => LucideIconKind.FileText,
        ToolCategory.Edit      => LucideIconKind.Pencil,
        ToolCategory.Command   => LucideIconKind.Terminal,
        ToolCategory.Search    => LucideIconKind.Search,
        ToolCategory.WebSearch => LucideIconKind.Globe,
        ToolCategory.Fetch     => LucideIconKind.Download,
        ToolCategory.Skill     => LucideIconKind.Sparkles,
        ToolCategory.Agent     => LucideIconKind.Bot,
        ToolCategory.Plan      => LucideIconKind.ListTodo,
        ToolCategory.Question  => LucideIconKind.MessageCircleQuestionMark,
        ToolCategory.Artefact  => LucideIconKind.File,
        ToolCategory.Work      => LucideIconKind.Ticket,
        ToolCategory.Memory    => LucideIconKind.Bookmark,
        ToolCategory.Session   => LucideIconKind.RotateCcw,
        ToolCategory.Flow      => LucideIconKind.Workflow,
        _                      => LucideIconKind.Puzzle,
    };

    public static LucideIconKind? ForFixedLabel(string label) => label switch {
        "Note"       => LucideIconKind.StickyNote,
        "Permission" => LucideIconKind.LockKeyhole,
        "Question"   => ForCategory(ToolCategory.Question),
        _            => null,
    };

    public static LucideIconKind CommandIcon => ForCategory(ToolCategory.Command);
    public static LucideIconKind NoteIcon => LucideIconKind.StickyNote;
    public static LucideIconKind PermissionIcon => LucideIconKind.LockKeyhole;
    public static LucideIconKind QuestionIcon => ForCategory(ToolCategory.Question);
}
