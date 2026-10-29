using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.App.Tests.Unit;

public class WizardAuthBridgesTests {
    static DiscoveredTenant Tenant(string login) => new() { OrgLogin = login, Origin = $"https://{login}.kcap.ai" };

    // ── tenant picker ────────────────────────────────────────────────────────

    [Test]
    public async Task PickAsync_publishes_the_tenants_and_completes_on_the_selection() {
        var picker = new WizardTenantPicker(new RecordingAuthProgress());
        DiscoveredTenant[]? offered = null;
        picker.SelectionRequested += tenants => offered = tenants;

        var pick = picker.PickAsync([Tenant("acme"), Tenant("globex")], TenantPickContext.None, CancellationToken.None);
        picker.Select(Tenant("globex"));

        await Assert.That((await pick.WaitAsync(TimeSpan.FromSeconds(5)))!.OrgLogin).IsEqualTo("globex");
        await Assert.That(offered!.Select(t => t.OrgLogin)).IsEquivalentTo(["acme", "globex"]);
    }

    [Test]
    public async Task Selecting_nothing_completes_the_await_with_no_tenant() {
        var picker = new WizardTenantPicker(new RecordingAuthProgress());

        var pick = picker.PickAsync([Tenant("acme")], TenantPickContext.None, CancellationToken.None);
        picker.Select(null);

        await Assert.That(await pick.WaitAsync(TimeSpan.FromSeconds(5))).IsNull();
    }

    // Returning null carries the promise that the user has been told why: discovery adds no line.
    [Test]
    public async Task Backing_out_is_reported_by_the_picker_itself() {
        var progress = new RecordingAuthProgress();
        var picker   = new WizardTenantPicker(progress);

        var pick = picker.PickAsync([Tenant("acme"), Tenant("globex")], TenantPickContext.None, CancellationToken.None);
        picker.Select(null);
        await pick.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(progress.Errors).Contains("No tenant selected.");
    }

    [Test]
    public async Task Cancelling_releases_a_pending_pick() {
        var picker = new WizardTenantPicker(new RecordingAuthProgress());
        using var cts = new CancellationTokenSource();

        var pick = picker.PickAsync([Tenant("acme")], TenantPickContext.None, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pick.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void The_synchronous_pick_is_not_supported() {
        var picker = new WizardTenantPicker(new RecordingAuthProgress());

        Assert.Throws<NotSupportedException>(() => picker.Pick([Tenant("acme")]));
    }

    // ── progress bridge ──────────────────────────────────────────────────────

    [Test]
    public async Task Every_progress_event_is_marshalled_through_the_post_delegate() {
        var posts = 0;
        var progress = new UiAuthProgress(action => {
            posts++;
            action();
        });

        var seen = new List<string>();
        progress.NoticeReceived     += m => seen.Add($"notice:{m}");
        progress.ErrorReceived      += m => seen.Add($"error:{m}");
        progress.BrowserOpened      += u => seen.Add($"browser:{u}");
        progress.DeviceCodeReceived += (code, uri, _) => seen.Add($"device:{code}@{uri}");
        progress.PollTicked         += () => seen.Add("tick");

        progress.Notice("hello");
        progress.Error("bad");
        progress.BrowserOpening("https://login.example");
        progress.DeviceCode("ABCD-1234", "https://github.com/login/device", "GitHub", prefilled: false);
        progress.PollTick();

        await Assert.That(posts).IsEqualTo(5);
        await Assert.That(seen).IsEquivalentTo([
            "notice:hello", "error:bad", "browser:https://login.example",
            "device:ABCD-1234@https://github.com/login/device", "tick"
        ]);
    }

    [Test]
    public async Task A_second_pick_cancels_the_one_it_displaces() {
        var picker = new WizardTenantPicker(new RecordingAuthProgress());

        var first  = picker.PickAsync([Tenant("acme")], TenantPickContext.None, CancellationToken.None);
        var second = picker.PickAsync([Tenant("globex")], TenantPickContext.None, CancellationToken.None);
        picker.Select(Tenant("globex"));

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.That((await second.WaitAsync(TimeSpan.FromSeconds(5)))!.OrgLogin).IsEqualTo("globex");
    }

    // ── intent → façade call mapping ─────────────────────────────────────────

    [Test]
    [Arguments("acme", "https://acme.kcap.ai")]
    [Arguments("https://acme.kcap.ai/sessions/9", "https://acme.kcap.ai")]
    [Arguments(" http://localhost:5108/ ", "http://localhost:5108")]
    // Core's own scheme rule for scheme-less input: loopback is http, everything else https.
    [Arguments("localhost:5108", "http://localhost:5108")]
    [Arguments("127.0.0.1:5108", "http://127.0.0.1:5108")]
    [Arguments("[::1]:5108", "http://[::1]:5108")]
    [Arguments("acme.kcap.ai", "https://acme.kcap.ai")]
    [Arguments("capacitor.example.com", "https://capacitor.example.com")]
    public async Task A_pasted_server_resolves_through_origin_then_slug_expansion(string typed, string expected) =>
        await Assert.That(WizardSignInOperation.ResolveServer(typed)).IsEqualTo(expected);
}
