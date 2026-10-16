using Capacitor.App.ViewModels;
using Lucide.Avalonia;

namespace Capacitor.App.Views;

/// Semantic category mappings shared by chat headers and pending prompts.
public static class ToolCategoryIcons {
    public static LucideIconKind ForCategory(ToolCategory category) => category switch {
        ToolCategory.Read      => LucideIconKind.FileSearch,
        ToolCategory.Edit      => LucideIconKind.Pencil,
        ToolCategory.Command   => LucideIconKind.Terminal,
        ToolCategory.Search    => LucideIconKind.Search,
        ToolCategory.WebSearch => LucideIconKind.Globe,
        ToolCategory.Fetch     => LucideIconKind.Link,
        ToolCategory.Skill     => LucideIconKind.BookOpen,
        ToolCategory.Agent     => LucideIconKind.Bot,
        ToolCategory.Plan      => LucideIconKind.ListTodo,
        ToolCategory.Question  => LucideIconKind.MessageCircleQuestionMark,
        ToolCategory.Artefact  => LucideIconKind.FileText,
        ToolCategory.Work      => LucideIconKind.Ticket,
        ToolCategory.Memory    => LucideIconKind.Brain,
        ToolCategory.Session   => LucideIconKind.RotateCcwClock,
        ToolCategory.Flow      => LucideIconKind.Workflow,
        _                      => LucideIconKind.Wrench,
    };

    public static LucideIconKind? ForFixedLabel(string label) => label switch {
        "Note"       => NoteIcon,
        "Permission" => PermissionIcon,
        "Question"   => ForCategory(ToolCategory.Question),
        AssistantThinkingItem.ThinkingLabel => ThinkingIcon,
        SystemNoteItem.AgentReportLabel or SystemNoteItem.AgentMessageLabel => AgentMessageIcon,
        _            => null,
    };

    public static LucideIconKind CommandIcon => ForCategory(ToolCategory.Command);
    public static LucideIconKind NoteIcon => LucideIconKind.StickyNote;
    public static LucideIconKind PermissionIcon => LucideIconKind.ShieldQuestionMark;
    public static LucideIconKind QuestionIcon => ForCategory(ToolCategory.Question);
    public static LucideIconKind AgentMessageIcon => LucideIconKind.BotMessageSquare;
    public static LucideIconKind ThinkingIcon => LucideIconKind.Sparkles;
}
