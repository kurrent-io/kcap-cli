using Avalonia.Media;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class ToolCategoryIconsTests {
    [Test]
    public async Task Every_category_has_parseable_path_data() {
        foreach (ToolCategory category in Enum.GetValues<ToolCategory>()) {
            var data = ToolCategoryIcons.ForCategory(category);
            await Assert.That(data).IsNotEmpty().Because($"{category}");
            await Assert.That(Geometry.Parse(data)).IsNotNull().Because($"{category}");
        }
    }

    [Test]
    [Arguments("Note")]
    [Arguments("Permission")]
    [Arguments("Question")]
    public async Task Fixed_labels_have_parseable_path_data(string label) {
        var data = ToolCategoryIcons.ForFixedLabel(label);
        await Assert.That(data).IsNotEmpty();
        await Assert.That(Geometry.Parse(data)).IsNotNull();
    }

    [Test]
    public async Task Unknown_fixed_label_is_empty() {
        await Assert.That(ToolCategoryIcons.ForFixedLabel("You")).IsEmpty();
        await Assert.That(ToolCategoryIcons.ForFixedLabel("")).IsEmpty();
    }

    [Test]
    public async Task Other_uses_puzzle_not_edit_glyph() {
        var other = ToolCategoryIcons.ForCategory(ToolCategory.Other);
        var edit = ToolCategoryIcons.ForCategory(ToolCategory.Edit);
        await Assert.That(other).IsNotEqualTo(edit);
        await Assert.That(other).Contains("H20 V12");
        await Assert.That(edit).Contains("M13,7 L17,11");
    }
}
