using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class KcapMcpRegistryReviewFlowTests {
    [Test]
    public async Task Resolve_accepts_read_only_servers_case_insensitively() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["Kcap-Review"], out var servers, out var rejected);

        await Assert.That(ok).IsTrue();
        await Assert.That(rejected).IsNull();
        await Assert.That(servers.Length).IsEqualTo(1);
        await Assert.That(servers[0]).IsEqualTo("kcap-review");   // canonical id
    }

    [Test]
    public async Task Resolve_dedupes_and_keeps_multiple_read_servers() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(
            ["kcap-review", "kcap-sessions", "kcap-review"], out var servers, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(servers.Length).IsEqualTo(2);
        await Assert.That(servers).Contains("kcap-review");
        await Assert.That(servers).Contains("kcap-sessions");
    }

    [Test]
    public async Task Resolve_rejects_flow_starting_server() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["kcap-review", "kcap-flows"], out var servers, out var rejected);

        await Assert.That(ok).IsFalse();
        await Assert.That(rejected).IsEqualTo("kcap-flows");
        await Assert.That(servers).IsEmpty();
    }

    [Test]
    public async Task Resolve_rejects_write_server_kcap_memory() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["kcap-memory"], out _, out var rejected);

        await Assert.That(ok).IsFalse();
        await Assert.That(rejected).IsEqualTo("kcap-memory");
    }

    [Test]
    public async Task Resolve_rejects_unknown_server() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["not-a-server"], out _, out var rejected);

        await Assert.That(ok).IsFalse();
        await Assert.That(rejected).IsEqualTo("not-a-server");
    }

    [Test]
    public async Task Resolve_treats_reserved_flow_result_id_as_a_satisfied_no_op() {
        // kcap-flow-result is always injected by the launcher and is not a registry entry; the
        // server's dynamic-flow policy legitimately lists it, so it must be accepted (not rejected)
        // and NOT re-emitted in the resolved servers. Every reviewer runtime shares this.
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(
            ["kcap-flow-result", "KCAP-FLOW-RESULT", "kcap-review"], out var servers, out var rejected);

        await Assert.That(ok).IsTrue();
        await Assert.That(rejected).IsNull();
        await Assert.That(servers).IsEquivalentTo(["kcap-review"]);
    }

    [Test]
    public async Task Resolve_null_or_empty_is_ok_empty() {
        var ok1 = KcapMcpRegistry.TryResolveReviewFlowAllowlist(null, out var s1, out var r1);
        await Assert.That(ok1).IsTrue();
        await Assert.That(s1).IsEmpty();
        await Assert.That(r1).IsNull();

        var ok2 = KcapMcpRegistry.TryResolveReviewFlowAllowlist([], out var s2, out _);
        await Assert.That(ok2).IsTrue();
        await Assert.That(s2).IsEmpty();
    }

    // Contract guard (static half): every auto-approvable server carries a tool classification.
    // (The dynamic half — cross-checking each server's live tools/list — is added in Task 4.)
    [Test]
    public async Task Every_auto_approvable_server_has_a_tool_classification() {
        foreach (var srv in KcapMcpRegistry.ReviewFlowAutoApprovableServers) {
            await Assert.That(KcapMcpRegistry.ReviewFlowUnattendedSafeTools.ContainsKey(srv)).IsTrue();
            await Assert.That(KcapMcpRegistry.ReviewFlowUnattendedSafeTools[srv]).IsNotEmpty();
        }
    }

    // The classification must never name a flow-starting or non-registered server.
    [Test]
    public async Task Classification_only_covers_auto_approvable_servers() {
        foreach (var srv in KcapMcpRegistry.ReviewFlowUnattendedSafeTools.Keys)
            await Assert.That(KcapMcpRegistry.ReviewFlowAutoApprovableServers.Contains(srv)).IsTrue();
    }
}
