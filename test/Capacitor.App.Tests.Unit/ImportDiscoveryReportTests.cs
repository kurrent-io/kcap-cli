using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class ImportDiscoveryReportTests {
    [Test]
    [Arguments("{}")]
    [Arguments("{\"repos\":null}")]
    [Arguments("{\"repos\":[null]}")]
    [Arguments("{\"repos\":[{\"owner\":\"org\",\"name\":\"repo\",\"sessions\":-1}]}")]
    [Arguments("{\"repos\":[],\"unmatched_sessions\":-1}")]
    public async Task Invalid_reports_are_rejected_before_the_view_reads_them(string json) =>
        await Assert.That(ImportDiscoveryReport.Parse(json)).IsNull();

    [Test]
    public async Task A_legacy_report_without_windows_still_loads() {
        var report = ImportDiscoveryReport.Parse("""{"repos":[{"owner":"org","name":"repo","sessions":3}],"unmatched_sessions":0}""");
        await Assert.That(report).IsNotNull();
        await Assert.That(report!.Repos.Single().Slug).IsEqualTo("org/repo");
        await Assert.That(report.Windows).IsEmpty();
        await Assert.That(report.Repos.Single().Vendors).IsNull();
    }

    [Test]
    [Arguments("{\"repos\":[{\"owner\":\"org\",\"name\":\"repo\",\"sessions\":1,\"vendors\":[null]}]}")]
    [Arguments("{\"repos\":[{\"owner\":\"org\",\"name\":\"repo\",\"sessions\":1,\"vendors\":[{\"vendor\":\"\",\"sessions\":1}]}]}")]
    [Arguments("{\"repos\":[{\"owner\":\"org\",\"name\":\"repo\",\"sessions\":1,\"vendors\":[{\"vendor\":\"claude\",\"sessions\":-1}]}]}")]
    public async Task An_invalid_harness_share_rejects_the_report(string json) =>
        await Assert.That(ImportDiscoveryReport.Parse(json)).IsNull();

    [Test]
    public async Task Each_harness_share_of_a_repository_loads_with_its_windows() {
        var report = ImportDiscoveryReport.Parse("""
            {"repos":[{"owner":"org","name":"repo","sessions":3,"windows":[{"since":"2026-09-01","sessions":2},{"since":null,"sessions":3}],
              "vendors":[{"vendor":"codex","sessions":3,"last_session_at":"2026-09-20T09:00:00.0000000Z",
                          "windows":[{"since":"2026-09-01","sessions":2},{"since":null,"sessions":3}]}]}],
             "unmatched_sessions":0,"windows":[{"since":"2026-09-01","sessions":2},{"since":null,"sessions":3}]}
            """);

        var share = report!.Repos.Single().Vendors!.Single();
        await Assert.That(share.Vendor).IsEqualTo("codex");
        await Assert.That(share.Sessions).IsEqualTo(3);
        await Assert.That(share.LastSessionAt).IsEqualTo(new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero));
        await Assert.That(share.Windows!.Select(w => w.Sessions)).IsEquivalentTo([2, 3]);
    }

    [Test]
    public async Task The_folders_behind_the_unmatched_count_load_with_their_windows() {
        var report = ImportDiscoveryReport.Parse("""
            {"repos":[],"unmatched_sessions":3,"windows":[{"since":null,"sessions":3}],
             "unmatched_folders":[{"folder":"/w/scratch","sessions":2,"windows":[{"since":null,"sessions":2}]},
                                  {"folder":null,"sessions":1,"windows":null}]}
            """);

        await Assert.That(report!.UnmatchedFolders!.Select(f => f.Folder)).IsEquivalentTo(["/w/scratch", null]);
        await Assert.That(report.UnmatchedFolders![0].Windows!.Single().Sessions).IsEqualTo(2);
        await Assert.That(ImportDiscoveryReport.Parse("""{"repos":[],"unmatched_sessions":0}""")!.UnmatchedFolders).IsEmpty();
        await Assert.That(ImportDiscoveryReport.Parse("""{"repos":[],"unmatched_sessions":1,"unmatched_folders":[{"folder":"","sessions":1}]}""")).IsNull();
    }
}
