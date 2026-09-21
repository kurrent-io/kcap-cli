using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Stroke PathData (24×24) for chat kind headers. Material-like at category grain — same
/// vocabulary as the web's GetToolIcon, not per vendor tool name. Strings only: Geometry is
/// an AvaloniaObject with thread affinity (see VendorIcons).
public static class ToolCategoryIcons {
    public static string ForCategory(ToolCategory category) => category switch {
        ToolCategory.Read      => "M6,3 H15 L19,7 V21 H6 Z M15,3 V7 H19",
        ToolCategory.Edit      => "M4,20 H8 L18.5,9.5 14.5,5.5 4,16 Z M13,7 L17,11",
        ToolCategory.Command   => "M3,5 H21 V17 H3 Z M8,20 H16 M12,17 V20",
        ToolCategory.Search    => "M10,4 A6,6 0 1 1 10,16 A6,6 0 1 1 10,4 M15,15 L20,20",
        ToolCategory.WebSearch => "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M3,12 H21 M12,3 C8,8 8,16 12,21 C16,16 16,8 12,3",
        ToolCategory.Fetch     => "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M3,12 H21",
        ToolCategory.Skill     => "M12,4 L13.2,9 H18.5 L14.3,12 L15.8,17.5 12,14.5 8.2,17.5 9.7,12 5.5,9 H10.8 Z",
        // Briefcase — "Task" chip; a two-head GroupWork stroke reads as a cut face at 12px.
        ToolCategory.Agent     => "M9,8 V6.5 A3,3 0 0 1 15,6.5 V8 H18 V19 H6 V8 Z M11,8 V7 A1,1 0 0 1 13,7 V8",
        ToolCategory.Plan      => "M6,6 H18 M6,11 H18 M6,16 H14 M5,5 H7 V7 H5 Z M5,10 H7 V12 H5 Z M5,15 H7 V17 H5 Z",
        ToolCategory.Question  => "M7,7 H17 V15 H13 L10,19 V15 H7 Z M12,10 V11 M12,14 H12.01",
        // Extension / puzzle — generic MCP and unknown tools; distinct from Edit and Agent at 12px.
        ToolCategory.Other     => "M8,4 H16 V8 H20 V12 H16 V16 H8 V12 H12 V8 H8 V4 Z",
        _                      => "M8,4 H16 V8 H20 V12 H16 V16 H8 V12 H12 V8 H8 V4 Z",
    };

    public static string ForFixedLabel(string label) => label switch {
        "Note"       => "M6,3 H15 L19,7 V21 H6 Z M15,3 V7 H19 M8,12 H16 M8,16 H14",
        "Permission" => "M7,10 V8 A5,5 0 0 1 17,8 V10 H19 V21 H5 V10 Z M12,14 V17",
        "Question"   => ForCategory(ToolCategory.Question),
        _            => "",
    };

    public static string NoteIcon => ForFixedLabel("Note");
    public static string PermissionIcon => ForFixedLabel("Permission");
    public static string QuestionIcon => ForFixedLabel("Question");
}
