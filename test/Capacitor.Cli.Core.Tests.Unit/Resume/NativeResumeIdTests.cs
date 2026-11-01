using Capacitor.Cli.Core.Resume;

namespace Capacitor.Cli.Core.Tests.Unit.Resume;

public class NativeResumeIdTests {
    const string Dashless = "0123456789abcdef0123456789abcdef";
    const string Dashed   = "01234567-89ab-cdef-0123-456789abcdef";

    [Test]
    [Arguments("claude", Dashless, Dashed)]
    [Arguments("claude", Dashed, Dashed)]
    [Arguments("codex", Dashless, Dashed)]
    [Arguments("Codex", Dashed, Dashed)]
    [Arguments("claude", "not-a-guid", null)]
    [Arguments("codex", "sess-1", null)]
    [Arguments("cursor", "sess-1", "sess-1")]
    [Arguments("copilot", Dashless, Dashed)]
    [Arguments("opencode", "ses_abc", "ses_abc")]
    [Arguments("cursor", "senc1abcdefghijklmnopqrstuvwxyz2", null)]
    [Arguments("kiro", "sess-1", null)]
    [Arguments("gemini", "sess-1", null)]
    [Arguments("grok", "sess-1", null)]
    [Arguments("pi", Dashless, null)]
    [Arguments("muse", Dashless, null)]
    [Arguments("antigravity", Dashless, null)]
    [Arguments("claude", "", null)]
    [Arguments("", Dashless, null)]
    public async Task Derives_the_vendors_resume_id(string vendor, string sessionId, string? expected) =>
        await Assert.That(NativeResumeId.For(vendor, sessionId)).IsEqualTo(expected);
}
