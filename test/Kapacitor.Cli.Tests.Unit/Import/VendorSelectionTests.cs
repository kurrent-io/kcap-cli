using Kapacitor.Cli.Commands;

namespace Kapacitor.Cli.Tests.Unit.Import;

public class VendorSelectionTests {
    [Test]
    public async Task no_vendor_flags_means_all_detected() {
        var r = VendorSelection.Parse([]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task claude_flag_selects_only_claude() {
        var r = VendorSelection.Parse(["--claude"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(1);
        await Assert.That(r.Vendors.Contains("claude")).IsTrue();
    }

    [Test]
    public async Task codex_flag_selects_only_codex() {
        var r = VendorSelection.Parse(["--codex"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(1);
        await Assert.That(r.Vendors.Contains("codex")).IsTrue();
    }

    [Test]
    public async Task cursor_flag_selects_only_cursor() {
        var r = VendorSelection.Parse(["--cursor"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(1);
        await Assert.That(r.Vendors.Contains("cursor")).IsTrue();
    }

    [Test]
    public async Task multiple_vendor_flags_are_additive() {
        var r = VendorSelection.Parse(["--claude", "--codex"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(2);
        await Assert.That(r.Vendors.Contains("claude")).IsTrue();
        await Assert.That(r.Vendors.Contains("codex")).IsTrue();
    }

    [Test]
    public async Task cursor_workspace_implies_cursor() {
        var r = VendorSelection.Parse(["--cursor-workspace", "/some/path"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(1);
        await Assert.That(r.Vendors.Contains("cursor")).IsTrue();
    }

    [Test]
    public async Task cursor_all_workspaces_implies_cursor() {
        var r = VendorSelection.Parse(["--cursor-all-workspaces"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(1);
        await Assert.That(r.Vendors.Contains("cursor")).IsTrue();
    }

    [Test]
    public async Task cursor_workspace_and_all_workspaces_together_is_error() {
        var r = VendorSelection.Parse(["--cursor-workspace", "/p", "--cursor-all-workspaces"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("mutually exclusive");
    }

    [Test]
    public async Task cursor_workspace_does_not_swallow_neighbouring_vendor_flag() {
        var r = VendorSelection.Parse(["--cursor-workspace", "--codex"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Contains("cursor")).IsTrue();
        await Assert.That(r.Vendors.Contains("codex")).IsTrue();
    }

    [Test]
    public async Task unknown_cursor_prefix_flag_is_rejected() {
        var r = VendorSelection.Parse(["--cursor-worskpace", "/p"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--cursor-worskpace");
        await Assert.That(r.Error!).Contains("Did you mean");
    }

    [Test]
    public async Task unknown_claude_prefix_flag_is_rejected() {
        var r = VendorSelection.Parse(["--claude-something"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--claude-something");
    }

    [Test]
    public async Task unknown_codex_prefix_flag_is_rejected() {
        var r = VendorSelection.Parse(["--codex-bogus"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--codex-bogus");
    }

    [Test]
    public async Task vendor_typo_one_edit_away_is_rejected() {
        var r = VendorSelection.Parse(["--curser"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--curser");
        await Assert.That(r.Error!).Contains("--cursor");
    }

    [Test]
    public async Task vendor_typo_two_edits_away_is_rejected() {
        var r = VendorSelection.Parse(["--codx"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--codx");
        await Assert.That(r.Error!).Contains("--codex");
    }

    [Test]
    public async Task arbitrary_unrelated_flag_passes_through() {
        var r = VendorSelection.Parse(["--server-url", "http://example.com", "--no-update-check"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task global_flags_pass_through() {
        var r = VendorSelection.Parse([
            "--all",
            "--org",
            "--repo", "x/y",
            "--cwd", "/p",
            "--since", "2026-01-01",
            "--session", "abc",
            "--min-lines", "20",
            "--private",
            "--yes",
            "-y",
            "--generate-summaries",
            "--server-url", "http://x",
            "--no-update-check",
            "--profile", "p"
        ]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Error).IsNull();
        await Assert.That(r.Vendors.Count).IsEqualTo(0);
    }
}
