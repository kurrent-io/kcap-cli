using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Pi;

public class PiReviewerExtensionTests {
    const string Ts = PiReviewerExtension.Content;

    [Test]
    public async Task Containment_is_component_wise_on_resolved_paths() {
        await Assert.That(Ts).Contains("realpathSync(manifest.root)");
        await Assert.That(Ts).Contains("relative(rootReal, target)");
        await Assert.That(Ts).Contains("rel.startsWith(\"..\" + sep)");
        await Assert.That(Ts).DoesNotContain("target.startsWith(rootReal");
    }

    [Test]
    public async Task Directory_walks_never_follow_a_symbolic_link() {
        await Assert.That(Ts).Contains("withFileTypes: true");
        await Assert.That(Ts).Contains("isSymbolicLink()");
    }

    [Test]
    public async Task Search_is_literal_and_builds_no_regular_expression() {
        await Assert.That(Ts).DoesNotContain("new RegExp(");
        await Assert.That(Ts).DoesNotContain("RegExp(");
    }

    [Test]
    public async Task The_only_process_it_spawns_is_a_manifest_server() {
        // One spawn site, in the shared client, fed only by manifest fields.
        await Assert.That(CountOf(Ts, "spawn(")).IsEqualTo(1);
        await Assert.That(Ts).DoesNotContain("exec(");
        await Assert.That(Ts).DoesNotContain("execSync(");
    }

    [Test]
    public async Task It_raises_no_dialog() {
        foreach (var call in new[] { "ui.confirm", "ui.select", "ui.input", "ui.editor" })
            await Assert.That(Ts).DoesNotContain(call);
    }

    [Test]
    public async Task A_failure_inside_the_factory_throws_rather_than_being_logged_and_skipped() {
        await Assert.That(Ts).Contains("throw new Error(\"kcap-reviewer: ");
        await Assert.That(Ts).DoesNotContain("console.error(\"[kcap-reviewer] could not register");
    }

    [Test]
    public async Task It_reports_the_active_tools_atomically_on_session_start() {
        await Assert.That(Ts).Contains("pi.on(\"session_start\"");
        await Assert.That(Ts).Contains("pi.getActiveTools()");
        await Assert.That(Ts).Contains("renameSync(");
    }

    [Test]
    public async Task It_carries_no_ticket_id() {
        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(Ts, @"\b(AI|DEV)-\d+")).IsFalse();
    }

    static int CountOf(string haystack, string needle) =>
        haystack.Split(needle, StringSplitOptions.None).Length - 1;
}
