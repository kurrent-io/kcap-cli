using Capacitor.Cli.Core.Plans;

namespace Capacitor.Cli.Core.Tests.Unit.Plans;

public class PlanToolNamesTests {
    [Test]
    [Arguments("update_plan_task")]
    [Arguments("set_plan_tasks")]
    [Arguments("declare_plan_document")]
    [Arguments("mcp__plugin_kcap_kcap-plans__update_plan_task")]
    [Arguments("mcp__kcap-plans__set_plan_tasks")]
    [Arguments("kcap-plans.declare_plan_document")]
    public async Task A_plan_write_is_recognised_bare_or_behind_a_vendors_server_prefix(string toolName) {
        await Assert.That(PlanToolNames.IsWrite(toolName)).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("get_plan")]
    [Arguments("mcp__plugin_kcap_kcap-plans__get_plan")]
    [Arguments("update_plan")]
    [Arguments("TodoWrite")]
    [Arguments("update_plan_task_extra")]
    public async Task A_read_or_an_unrelated_tool_is_not_a_plan_write(string? toolName) {
        await Assert.That(PlanToolNames.IsWrite(toolName)).IsFalse();
    }
}
