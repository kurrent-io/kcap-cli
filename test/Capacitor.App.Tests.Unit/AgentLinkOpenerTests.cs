using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class AgentLinkOpenerTests {
    const string Server = "https://kurrent.kcap.ai";

    [Test]
    public async Task An_agent_page_on_the_app_server_yields_its_id() {
        await Assert.That(AgentLinkOpener.AgentIdOf("https://kurrent.kcap.ai/agents/a83f586c", Server)).IsEqualTo("a83f586c");
        await Assert.That(AgentLinkOpener.AgentIdOf("https://KURRENT.kcap.ai/agents/a83f586c/?x=1#y", Server + "/")).IsEqualTo("a83f586c");
        await Assert.That(AgentLinkOpener.AgentIdOf("https://host/base/agents/a1", "https://host/base")).IsEqualTo("a1");
    }

    [Test]
    public async Task Any_other_link_yields_no_id() {
        foreach (var url in new[] {
                     "https://other.kcap.ai/agents/a1", "http://kurrent.kcap.ai/agents/a1", "https://kurrent.kcap.ai:8443/agents/a1",
                     "https://kurrent.kcap.ai/agents/", "https://kurrent.kcap.ai/agents/a1/sessions", "https://kurrent.kcap.ai/work-items/w1",
                     "https://kurrent.kcap.ai/x/agents/a1", "not a url",
                 })
            await Assert.That(AgentLinkOpener.AgentIdOf(url, Server)).IsNull().Because(url);
        await Assert.That(AgentLinkOpener.AgentIdOf("https://kurrent.kcap.ai/agents/a1", null)).IsNull();
    }

    [Test]
    public async Task A_known_agent_opens_in_the_app_and_an_unknown_one_in_the_browser() {
        var browser = new RecordingOpener();
        var asked = new List<string>();
        var opener = new AgentLinkOpener(browser, Server, id => { asked.Add(id); return id == "known"; });

        opener.Open("https://kurrent.kcap.ai/agents/known");
        opener.Open("https://kurrent.kcap.ai/agents/unknown");
        opener.Open("https://github.com/kurrent-io/kcap-cli");

        await Assert.That(asked).IsEquivalentTo(["known", "unknown"]);
        await Assert.That(browser.Opened).IsEquivalentTo(["https://kurrent.kcap.ai/agents/unknown", "https://github.com/kurrent-io/kcap-cli"]);
    }
}
