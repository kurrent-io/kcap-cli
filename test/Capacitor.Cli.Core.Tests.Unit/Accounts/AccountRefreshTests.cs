using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalEdition;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.Cli.Core.Tests.Unit.Accounts.Readings.ReadingFixtures;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountRefreshTests {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeTimeProvider _time = new(T0);

    AccountStore? _store;
    AccountStore Store => _store ??= new(Tmp.PathTo("accounts"));

    static VendorAccount Codex(string? email = null, string? id = null, long generation = 0, string label = ".codex-a", bool? userLabel = null) =>
        new("a", HarnessId.Codex, "/h/.codex-a", label, DateTimeOffset.UnixEpoch,
            Generation: generation, Email: email, VendorAccountId: id, LabelSetByUser: userLabel);

    static VendorAccount Claude(string? email = null, string? orgId = null) =>
        new("a", HarnessId.Claude, "/h/.claude-a", ".claude-a", DateTimeOffset.UnixEpoch, Email: email, OrgId: orgId);

    void Register(VendorAccount account) => Store.Mutate(r => (r with { Accounts = [account] }, 0));

    VendorAccount Account => Store.Load().Accounts.Single();

    ReadingFile? Reading => Store.Readings.LoadAll().TryGetValue("a", out var file) ? file : null;

    Task<RefreshOutcome> Refresh(IAccountProbe probe) => AccountRefresh.RunAsync(Store, "a", probe, _time, CancellationToken.None);

    Task<RefreshOutcome> Refresh(ProbeResult result) => Refresh(FakeAccountProbe.Returning(result));

    static ProbeResult Identity(string? email, string? accountId = null, ReadingObservation? limits = null, string? orgId = null, string? limitsError = null) =>
        new(SignInStates.SignedIn, new AccountIdentity(email, orgId, null, "pro", accountId), null, limits, limitsError);

    static ProbeResult Failed(string error = "boom", ReadingObservation? limits = null) => new(null, null, error, limits, null);

    static ReadingObservation Limits(int at, double used, int windowMinutes = 300) =>
        Obs(at, fresh: true, windows: [W("codex", windowMinutes, used, At(at + windowMinutes))]);

    bool Seed(string session, double used = 50) =>
        Store.Readings.TryRecord("a", (f, _) => f with {
            Reading  = ReadingMerge.Apply(f.Reading, Limits(0, used)),
            Sessions = [.. f.Sessions, new ReadingSession(session, null)],
        });

    /// <summary>The older refresh takes its sequence number first and answers last.</summary>
    async Task Overlap(ProbeResult older, ProbeResult newer) {
        var first   = new FakeAccountProbe();
        var pending = Refresh(first);
        await first.Started;

        await Refresh(newer);
        first.Complete(older);
        await pending;
    }

    [Test]
    public async Task First_probe_records_identity_without_bumping_generation() {
        Register(Codex());
        await Assert.That(Seed("s1")).IsTrue();

        var outcome = await Refresh(Identity("a@example.com", "idA"));

        var account = Account;
        await Assert.That(outcome.Found).IsTrue();
        await Assert.That(outcome.Steps.Select(s => (s.Name, s.Succeeded))).IsEquivalentTo([("identity", true), ("limits", true)]);
        await Assert.That(account.Generation).IsEqualTo(0L);
        await Assert.That(account.Email).IsEqualTo("a@example.com");
        await Assert.That(account.VendorAccountId).IsEqualTo("idA");
        await Assert.That(account.Plan).IsEqualTo("pro");
        await Assert.That(account.SignIn).IsEqualTo(SignInStates.SignedIn);
        await Assert.That(account.Label).IsEqualTo("a@example.com");
        await Assert.That(account.LabelSetByUser == false).IsTrue();
        await Assert.That(account.GenerationStartedAt).IsNull();
        await Assert.That(account.RefreshError).IsNull();
        await Assert.That(account.CommittedProbeSeq).IsEqualTo(1L);
        await Assert.That(Reading!.Sessions.Single().SessionId).IsEqualTo("s1");
    }

    [Test]
    public async Task Login_change_bumps_generation_and_deletes_reading() {
        Register(Claude("a@example.com", "org-a"));
        Seed("s1");
        Seed("s2");

        var probe = new FakeAccountProbe { BeforeReturn = () => _time.Advance(TimeSpan.FromMinutes(1)) };
        probe.Complete(Identity("b@example.com", orgId: "org-b"));
        var outcome = await Refresh(probe);

        var account = Account;
        await Assert.That(outcome.Steps.Select(s => s.Name)).IsEquivalentTo(["identity"]);
        await Assert.That(account.Generation).IsEqualTo(1L);
        await Assert.That(account.GenerationStartedAt).IsEqualTo(T0);
        await Assert.That(account.RefreshedAt).IsEqualTo(T0.AddMinutes(1));
        await Assert.That(account.Email).IsEqualTo("b@example.com");
        await Assert.That(account.OrgId).IsEqualTo("org-b");
        await Assert.That(account.Label).IsEqualTo("b@example.com");
        await Assert.That(Reading).IsNull();
    }

    [Test]
    public async Task Claude_org_change_alone_is_a_login_change() {
        Register(Claude("a@example.com", "org-a"));

        await Refresh(Identity("a@example.com", orgId: "org-b"));

        await Assert.That(Account.Generation).IsEqualTo(1L);
    }

    [Test]
    public async Task Single_refresh_discovering_login_B_leaves_B_identity_and_B_limits() {
        Register(Codex("a@example.com", "idA"));
        Seed("s1", used: 90);

        await Refresh(Identity("b@example.com", "idB", Limits(5, 5, windowMinutes: 10080)));

        var reading = Reading!;
        await Assert.That(Account.Generation).IsEqualTo(1L);
        await Assert.That(Account.Email).IsEqualTo("b@example.com");
        await Assert.That(reading.Reading.Generation).IsEqualTo(1L);
        await Assert.That(reading.Reading.Windows.Single().WindowMinutes).IsEqualTo(10080);
        await Assert.That(reading.Sessions).IsEmpty();
        await Assert.That(reading.RefreshOrigins!.Single()).IsEqualTo(At(5));
    }

    [Test]
    public async Task Overlapping_refreshes_completing_in_reverse_order_leave_the_newer_login() {
        Register(Codex("a@example.com", "idA"));

        await Overlap(older: Identity("b@example.com", "idB"), newer: Identity("c@example.com", "idC"));

        var account = Account;
        await Assert.That(account.Email).IsEqualTo("c@example.com");
        await Assert.That(account.VendorAccountId).IsEqualTo("idC");
        await Assert.That(account.Generation).IsEqualTo(1L);
        await Assert.That(account.CommittedProbeSeq).IsEqualTo(2L);
        await Assert.That(account.ProbeSeq).IsEqualTo(2L);
    }

    [Test]
    public async Task Superseded_refresh_drops_its_limits() {
        Register(Codex("a@example.com", "idA"));

        await Overlap(older: Identity("a@example.com", "idA", Limits(1, 77)), newer: Identity("a@example.com", "idA"));

        await Assert.That(Reading).IsNull();
    }

    [Test]
    public async Task Failed_probe_sets_error_and_keeps_identity_and_reading() {
        Register(Codex("a@example.com", "idA") with { SignIn = SignInStates.SignedIn });
        Seed("s1", used: 42);

        var outcome = await Refresh(Failed("codex not found"));

        var account = Account;
        await Assert.That(outcome.Steps.Single(s => s.Name == "identity").Succeeded).IsFalse();
        await Assert.That(account.RefreshError).IsEqualTo("codex not found");
        await Assert.That(account.Email).IsEqualTo("a@example.com");
        await Assert.That(account.SignIn).IsEqualTo(SignInStates.SignedIn);
        await Assert.That(account.Generation).IsEqualTo(0L);
        await Assert.That(account.RefreshedAt).IsEqualTo(T0);
        await Assert.That(Reading!.Reading.Windows.Single().UsedPercent).IsEqualTo(42.0);
    }

    [Test]
    public async Task Failed_probe_merges_limits_only_under_unchanged_generation() {
        Register(Codex("a@example.com", "idA"));

        await Refresh(Failed(limits: Limits(1, 20)));
        await Assert.That(Reading!.Reading.Windows.Single().UsedPercent).IsEqualTo(20.0);

        var moved = new FakeAccountProbe {
            BeforeReturn = () => Store.Mutate(r => (r with { Accounts = [.. r.Accounts.Select(a => a with { Generation = 1 })] }, 0)),
        };
        moved.Complete(Failed(limits: Limits(2, 30, windowMinutes: 10080)));
        await Refresh(moved);

        await Assert.That(Reading!.Reading.Windows.Any(w => w.WindowMinutes == 10080)).IsFalse();
    }

    [Test]
    public async Task Limits_failure_records_limits_error_and_keeps_identity() {
        Register(Codex("a@example.com", "idA"));

        var outcome = await Refresh(Identity("a@example.com", "idA", limitsError: "account/rateLimits/read: Method not found"));

        var account = Account;
        await Assert.That(outcome.Steps.Select(s => (s.Name, s.Succeeded))).IsEquivalentTo([("identity", true), ("limits", false)]);
        await Assert.That(account.LimitsError).IsEqualTo("account/rateLimits/read: Method not found");
        await Assert.That(account.RefreshError).IsNull();
        await Assert.That(account.Email).IsEqualTo("a@example.com");

        await Refresh(Identity("a@example.com", "idA", Limits(1, 10)));
        await Assert.That(Account.LimitsError).IsNull();
    }

    [Test]
    public async Task Unchanged_refresh_still_bumps_revision_and_stamps_refreshedAt() {
        Register(Codex("a@example.com", "idA"));
        await Refresh(Identity("a@example.com", "idA"));
        var before = Store.Load().Revision;
        _time.Advance(TimeSpan.FromMinutes(1));

        var outcome = await Refresh(Identity("a@example.com", "idA"));

        await Assert.That(Store.Load().Revision).IsGreaterThan(before);
        await Assert.That(outcome.Revision).IsEqualTo(Store.Load().Revision);
        await Assert.That(Account.RefreshedAt).IsEqualTo(At(1));
    }

    [Test]
    public async Task Refresh_of_an_unknown_account_is_not_found() {
        Register(Codex());

        var outcome = await AccountRefresh.RunAsync(Store, "zzz", FakeAccountProbe.Returning(Identity("a@example.com")), _time, CancellationToken.None);

        await Assert.That(outcome.Found).IsFalse();
        await Assert.That(outcome.Steps).IsEmpty();
    }

    [Test]
    public async Task Account_removed_during_the_probe_commits_nothing() {
        Register(Codex("a@example.com", "idA"));
        var probe = new FakeAccountProbe { BeforeReturn = () => Store.Mutate(r => (r with { Accounts = [] }, 0)) };
        probe.Complete(Identity("a@example.com", "idA", Limits(1, 10)));

        var outcome = await Refresh(probe);

        await Assert.That(outcome.Found).IsFalse();
        await Assert.That(Store.Load().Accounts).IsEmpty();
        await Assert.That(Reading).IsNull();
    }

    [Test]
    public async Task A_session_seen_before_the_switch_records_into_the_new_generation() {
        Register(Codex("a@example.com", "idA"));
        Seed("s1");

        await Refresh(Identity("b@example.com", "idB"));

        await Assert.That(Seed("s1")).IsTrue();
    }

    [Test]
    public async Task Producer_committing_just_before_the_switch_is_not_fenced() {
        Register(Codex("a@example.com", "idA"));
        using var holding  = new ManualResetEventSlim();
        Task<bool>? producer = null;

        var probe = new FakeAccountProbe {
            BeforeReturn = () => {
                producer = Task.Run(() => Seed("S"));
                holding.Wait(TimeSpan.FromSeconds(10));
            },
        };
        Store.Readings.AfterMembershipCheck = () => {
            holding.Set();
            Thread.Sleep(300);
        };
        probe.Complete(Identity("b@example.com", "idB"));

        await Refresh(probe);
        Store.Readings.AfterMembershipCheck = null;

        await Assert.That(await producer!).IsTrue();
        await Assert.That(Reading).IsNull();
        await Assert.That(Account.GenerationStartedAt).IsNotNull();
        await Assert.That(Seed("S")).IsTrue();
    }

    [Test]
    public async Task User_label_survives_identity() {
        Register(Codex(label: "Mine", userLabel: true));

        await Refresh(Identity("a@example.com"));

        await Assert.That(Account.Label).IsEqualTo("Mine");
        await Assert.That(Account.LabelSetByUser == true).IsTrue();
    }

    [Test]
    public async Task Legacy_renamed_label_survives_the_first_refresh() {
        Tmp.CreateFile("accounts/accounts.json", """
            { "version": 1, "revision": 3, "accounts": [
                { "id": "a", "vendor": "Codex", "directory": "/h/.codex-a", "label": "Work",
                  "added_at": "2026-01-01T00:00:00Z", "wired_at": "2026-01-02T00:00:00Z", "wiring_failure": null },
                { "id": "b", "vendor": "Codex", "directory": "/h/.codex-b", "label": ".codex-b",
                  "added_at": "2026-01-01T00:00:00Z", "wired_at": "2026-01-02T00:00:00Z", "wiring_failure": null }
            ] }
            """);

        await Refresh(Identity("a@example.com"));
        await AccountRefresh.RunAsync(Store, "b", FakeAccountProbe.Returning(Identity("b@example.com")), _time, CancellationToken.None);

        var accounts = Store.Load().Accounts;
        await Assert.That(accounts.Single(a => a.Id == "a").Label).IsEqualTo("Work");
        await Assert.That(accounts.Single(a => a.Id == "a").LabelSetByUser == true).IsTrue();
        await Assert.That(accounts.Single(a => a.Id == "b").Label).IsEqualTo("b@example.com");
    }

    [Test]
    [Arguments("success-then-error")]
    [Arguments("error-then-success")]
    [Arguments("success-then-limits-error")]
    public async Task Superseded_probe_leaves_errors_as_the_newer_probe_set_them(string order) {
        Register(Codex("a@example.com", "idA"));
        var ok = Identity("a@example.com", "idA", Limits(1, 10));

        var (older, newer, refreshError, limitsError) = order switch {
            "success-then-error" => (ok, Failed("newer failed"), "newer failed", (string?) null),
            "error-then-success" => (Failed("older failed"), ok, (string?) null, (string?) null),
            _                    => (ok, Identity("a@example.com", "idA", limitsError: "newer limits failed"), null, "newer limits failed"),
        };

        await Overlap(older, newer);

        await Assert.That(Account.RefreshError).IsEqualTo(refreshError);
        await Assert.That(Account.LimitsError).IsEqualTo(limitsError);
    }

    [Test]
    public async Task Same_email_different_codex_account_id_bumps_generation() {
        Register(Codex("a@example.com", "idA"));

        await Refresh(Identity("a@example.com", "idB"));

        await Assert.That(Account.Generation).IsEqualTo(1L);
        await Assert.That(Account.VendorAccountId).IsEqualTo("idB");
    }

    [Test]
    public async Task Codex_probe_without_account_id_compares_emails_only() {
        Register(Codex("a@example.com", "idA"));

        await Refresh(Identity("a@example.com"));
        await Assert.That(Account.Generation).IsEqualTo(0L);
        await Assert.That(Account.VendorAccountId).IsEqualTo("idA");

        await Refresh(Identity("b@example.com"));
        await Assert.That(Account.Generation).IsEqualTo(1L);
        await Assert.That(Account.VendorAccountId).IsNull();
    }

    [Test]
    public async Task Email_change_without_id_then_id_arrives_bumps_generation_once() {
        Register(Codex("a@example.com", "idA"));

        await Refresh(Identity("b@example.com"));
        await Assert.That(Account.Email).IsEqualTo("b@example.com");
        await Assert.That(Account.VendorAccountId).IsNull();

        await Refresh(Identity("b@example.com", "idB"));
        await Assert.That(Account.VendorAccountId).IsEqualTo("idB");
        await Assert.That(Account.Generation).IsEqualTo(1L);
    }

    [Test]
    public async Task Null_email_with_matching_id_is_not_a_login_change() {
        Register(Codex("a@example.com", "idX"));

        await Refresh(Identity(null, "idX"));
        await Assert.That(Account.Generation).IsEqualTo(0L);
        await Assert.That(Account.Email).IsEqualTo("a@example.com");

        await Refresh(Identity("a@example.com", "idX"));
        await Assert.That(Account.Generation).IsEqualTo(0L);
    }

    [Test]
    public async Task Limits_step_without_a_chatgpt_login_succeeds_with_the_probe_s_note() {
        Register(Codex("a@example.com", "idA"));

        var outcome = await Refresh(new ProbeResult(SignInStates.SignedOut, null, null, null, null, LimitsNote: "not signed in"));

        var limits = outcome.Steps.Single(s => s.Name == "limits");
        await Assert.That(limits.Succeeded).IsTrue();
        await Assert.That(limits.Detail).IsEqualTo("not signed in");
        await Assert.That(Account.LimitsError).IsNull();
    }

    [Test]
    public async Task Signed_out_answer_keeps_the_recorded_identity() {
        Register(Codex("a@example.com", "idA") with { SignIn = SignInStates.SignedIn });

        await Refresh(new ProbeResult(SignInStates.SignedOut, null, null, null, "not signed in"));

        await Assert.That(Account.SignIn).IsEqualTo(SignInStates.SignedOut);
        await Assert.That(Account.Email).IsEqualTo("a@example.com");
        await Assert.That(Account.Generation).IsEqualTo(0L);
        await Assert.That(Account.RefreshError).IsNull();
    }

    [Test]
    public async Task Refresh_merge_records_its_origin_and_a_superseded_probe_records_none() {
        Register(Codex("a@example.com", "idA"));
        await Refresh(Identity("a@example.com", "idA", Limits(5, 10)));
        await Assert.That(Reading!.RefreshOrigins).IsEquivalentTo([At(5)]);

        await Overlap(
            older: Identity("a@example.com", "idA", Limits(7, 20)),
            newer: Identity("a@example.com", "idA", Limits(9, 30)));

        await Assert.That(Reading!.RefreshOrigins).IsEquivalentTo([At(5), At(9)]);
    }

    [Test]
    public async Task Refresh_origins_keep_the_last_sixteen() {
        Register(Codex("a@example.com", "idA"));

        for (var at = 1; at <= 20; at++) await Refresh(Identity("a@example.com", "idA", Limits(at, at)));

        await Assert.That(Reading!.RefreshOrigins).IsEquivalentTo(Enumerable.Range(5, 16).Select(At).ToArray());
    }

    sealed class SimulatedCrash : Exception;

    [Test]
    public async Task Failed_registry_save_leaves_reading_and_generation_untouched() {
        Register(Codex("a@example.com", "idA"));
        Seed("s1");
        var saves = 0;

        using (FaultPoints.Arm(point => {
                   if (point == $"rename:{Store.RegistryPath}" && ++saves == 2) throw new SimulatedCrash();
               })) {
            await Assert.That(async () => await Refresh(Identity("b@example.com", "idB", Limits(1, 5, windowMinutes: 10080)))).Throws<SimulatedCrash>();
        }

        var account = Account;
        await Assert.That(account.Generation).IsEqualTo(0L);
        await Assert.That(account.GenerationStartedAt).IsNull();
        await Assert.That(account.Email).IsEqualTo("a@example.com");
        await Assert.That(Reading!.Reading.Generation).IsEqualTo(0L);
        await Assert.That(Reading!.Sessions.Single().SessionId).IsEqualTo("s1");
        await Assert.That(Reading!.Reading.Windows.Single().WindowMinutes).IsEqualTo(300);
    }

    [Test]
    public async Task Refresh_after_crash_resets_an_older_generation_reading() {
        Register(Codex(generation: 1));
        await Assert.That(Seed("s1", used: 90)).IsTrue();
        Store.Mutate(r => (r with { Accounts = [Codex(generation: 2)] }, 0));

        await Refresh(Identity("a@example.com", "idA", Limits(10, 5, windowMinutes: 10080)));

        var reading = Reading!;
        await Assert.That(reading.Reading.Generation).IsEqualTo(2L);
        await Assert.That(reading.Reading.Windows.Single().WindowMinutes).IsEqualTo(10080);
        await Assert.That(reading.Sessions).IsEmpty();
    }

    static ProbeResult WithTrust(ProbeResult result, HookTrustObservation? trust) => result with { HookTrust = trust };

    static readonly HookTrustObservation Trusted = new(HookTrustStates.Trusted, "fp1");

    [Test]
    public async Task A_trust_answer_is_stored_with_its_fingerprint() {
        Register(Codex("a@example.com", "idA"));

        await Refresh(WithTrust(Identity("a@example.com", "idA"), Trusted));

        await Assert.That(Account.HookTrust).IsEqualTo(HookTrustStates.Trusted);
        await Assert.That(Account.HookTrustFingerprint).IsEqualTo("fp1");
    }

    [Test]
    public async Task No_trust_answer_keeps_the_stored_pair() {
        Register(Codex("a@example.com", "idA") with { HookTrust = HookTrustStates.Untrusted, HookTrustFingerprint = "old" });

        await Refresh(Identity("a@example.com", "idA"));

        await Assert.That(Account.HookTrust).IsEqualTo(HookTrustStates.Untrusted);
        await Assert.That(Account.HookTrustFingerprint).IsEqualTo("old");
    }

    [Test]
    public async Task A_trust_answer_commits_beside_an_identity_error() {
        Register(Codex("a@example.com", "idA"));

        await Refresh(WithTrust(Failed(), Trusted));

        await Assert.That(Account.RefreshError).IsEqualTo("boom");
        await Assert.That(Account.HookTrust).IsEqualTo(HookTrustStates.Trusted);
        await Assert.That(Account.HookTrustFingerprint).IsEqualTo("fp1");
    }

    [Test]
    public async Task A_superseded_probe_stores_no_trust() {
        Register(Codex("a@example.com", "idA"));

        await Overlap(older: WithTrust(Identity("a@example.com", "idA"), Trusted), newer: Identity("a@example.com", "idA"));

        await Assert.That(Account.HookTrust).IsNull();
    }

    [Test]
    public async Task Codex_refresh_steps_are_still_identity_and_limits() {
        Register(Codex("a@example.com", "idA"));

        var outcome = await Refresh(WithTrust(Identity("a@example.com", "idA"), Trusted));

        await Assert.That(outcome.Steps.Select(s => s.Name)).IsEquivalentTo(["identity", "limits"]);
    }
}
