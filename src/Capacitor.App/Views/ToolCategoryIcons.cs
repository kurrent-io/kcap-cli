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
        ToolCategory.Skill     => "M12,3 L13.5,9 H20 L15,12.5 17,19 12,15 7,19 9,12.5 4,9 H10.5 Z",
        ToolCategory.Agent     => "M8,10 A3,3 0 1 0 8,4 A3,3 0 1 0 8,10 M16,10 A3,3 0 1 0 16,4 A3,3 0 1 0 16,10 M4,20 C4,16 20,16 20,20",
        ToolCategory.Plan      => "M5,5 H19 M5,10 H19 M5,15 H14 M4,4 H6 V6 H4 Z M4,9 H6 V11 H4 Z M4,14 H6 V16 H4 Z",
        ToolCategory.Question  => "M6,6 H18 V16 H13 L9,20 V16 H6 Z M12,9 V10 M12,13 H12.01",
        ToolCategory.Other     => "M14.5,4 L19,9 9.5,18.5 5,19 5.5,14.5 Z M12,7 L16,11",
        _                      => "M14.5,4 L19,9 9.5,18.5 5,19 5.5,14.5 Z M12,7 L16,11",
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
