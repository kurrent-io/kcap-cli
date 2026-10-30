using Capacitor.Cli.Core.Pages;

namespace Capacitor.Cli.Core.Tests.Unit.Pages;

public class PageToolNamesTests {
    [Test]
    [Arguments("publish_page")]
    [Arguments("mcp__kcap-pages__publish_page")]
    [Arguments("mcp__plugin_kcap_kcap-pages__publish_page")]
    public async Task A_publish_is_recognised_bare_or_behind_a_vendors_server_prefix(string toolName) {
        await Assert.That(PageToolNames.IsPublish(toolName)).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("republish_pages")]
    [Arguments("republish_page")]
    [Arguments("list_my_pages")]
    [Arguments("publish_page_extra")]
    public async Task Anything_else_is_not_a_publish(string? toolName) {
        await Assert.That(PageToolNames.IsPublish(toolName)).IsFalse();
    }
}
