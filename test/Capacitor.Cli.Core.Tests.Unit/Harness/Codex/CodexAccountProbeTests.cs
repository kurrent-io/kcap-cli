using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

[ParallelLimiter<SubprocessLimit>]
public class CodexAccountProbeTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    const string Initialized = """{"jsonrpc":"2.0","id":1,"result":{"userAgent":"stub"}}""";

    const string ChatGptAccount = """{"jsonrpc":"2.0","id":2,"result":{"account":{"type":"chatgpt","email":"a@example.com","planType":"pro"},"requiresOpenaiAuth":true,"workspaceRouting":{"accountRoutingOverride":"NO_CONSTRAINT","backendOrigin":"https://chatgpt.com","chatgptAccountId":"acct-1"}}}""";

    const string RateLimits = """{"jsonrpc":"2.0","id":3,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":12,"windowDurationMins":300,"resetsAt":1793449575},"secondary":null,"planType":"pro"},"rateLimitsByLimitId":null,"ordinaryUsageAllowed":true,"accountId":"acct-1"}}""";

    // The stub answers hooks/list with the id it read; the body is the JSON after the id.
    static string Answer(string rest) => $"printf '%s\\n' \"{{\\\"jsonrpc\\\":\\\"2.0\\\",\\\"id\\\":$id,{rest.Replace("\"", "\\\"")}}}\"";

    static readonly string HooksError = Answer("""
        "error":{"code":-32601,"message":"Method not found"}
        """.Trim());

    static string HooksResult(string status, params string[] events) =>
        Answer($$"""
            "result":{"data":[{"cwd":"/x","hooks":[{{string.Join(",", events.Select(e => $$"""{"key":"k:{{e}}","eventName":"{{e}}","command":"kcap hook --codex","currentHash":"sha256:abc","trustStatus":"{{status}}"}"""))}}],"warnings":[],"errors":[]}]}
            """.Trim());

    static readonly string[] Critical = ["sessionStart", "stop", "permissionRequest"];

    string Log     => Tmp.PathTo("requests.log");
    string EnvFile => Tmp.PathTo("env.txt");

    /// <summary>A stub app-server answering each request line by its method; <paramref name="onAccountRead"/>
    /// replaces the plain answer to <c>account/read</c>.</summary>
    CodexAccountProbe Probe(string account = ChatGptAccount, string limits = RateLimits, string? onAccountRead = null, TimeSpan? deadline = null, string? hooksList = null) {
        var accountRead = onAccountRead ?? $"printf '%s\\n' '{account}'";
        var hooksAnswer = hooksList ?? HooksError;
        Tmp.CreateExecutable("bin/codex", $$"""
            #!/bin/sh
            printf '%s' "${CODEX_HOME-<unset>}" > '{{EnvFile}}'
            printf '%s\n' "$*" >> '{{Log}}'
            while IFS= read -r line; do
              printf '%s\n' "$line" >> '{{Log}}'
              case "$line" in
                *'"method":"initialize"'*) printf '%s\n' '{{Initialized}}' ;;
                *'"method":"account/read"'*) {{accountRead}} ;;
                *'"method":"account/rateLimits/read"'*) printf '%s\n' '{{limits}}' ;;
                *'"method":"hooks/list"'*)
                  id=${line#*'"id":'}; id=${id%%,*}
                  {{hooksAnswer}} ;;
              esac
            done
            """);

        return new CodexAccountProbe(TestHarnesses.Under(Home, BinaryProbe.Searching(Tmp.PathTo("bin"))), Home, TimeProvider.System) {
            Deadline = deadline ?? TimeSpan.FromSeconds(10),
        };
    }

    VendorAccount Account(string directory = ".codex-work") => new("a", HarnessId.Codex, Home.PathTo(directory), directory, DateTimeOffset.UnixEpoch);

    [Test]
    public async Task Reads_identity_and_limits() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");

        var result = await Probe().ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.IdentityError).IsNull();
        await Assert.That(result.LimitsError).IsNull();
        await Assert.That(result.SignIn).IsEqualTo(SignInStates.SignedIn);
        await Assert.That(result.Identity).IsEqualTo(new AccountIdentity("a@example.com", null, null, "pro", "acct-1"));
        await Assert.That(result.Limits!.Windows.Single()).IsEqualTo(new UsageWindowDto("codex", 300, 12, DateTimeOffset.FromUnixTimeSeconds(1793449575), null));
        await Assert.That(result.Limits.LimitStateKnown).IsTrue();

        var log = File.ReadAllLines(Log);
        await Assert.That(log[0]).IsEqualTo("app-server");
        await Assert.That(log.Single(l => l.Contains("\"initialize\""))).Contains("\"clientInfo\":{\"name\":\"kcap\"");
        await Assert.That(log.Single(l => l.Contains("account/read"))).Contains("\"params\":{}");
        await Assert.That(log.Single(l => l.Contains("account/rateLimits/read"))).Contains("\"params\":{\"excludeResetCreditDetails\":true}");
        await Assert.That(File.ReadAllText(EnvFile)).IsEqualTo(Home.PathTo(".codex-work"));
    }

    [Test]
    public async Task Codex_home_is_removed_for_the_default_directory() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");

        await Probe().ProbeAsync(Account(".codex"), CancellationToken.None);

        await Assert.That(File.ReadAllText(EnvFile)).IsEqualTo("<unset>");
    }

    [Test]
    public async Task Signed_out_account_is_authoritative() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        var probe = Probe(account: """{"jsonrpc":"2.0","id":2,"result":{"account":null,"requiresOpenaiAuth":true,"workspaceRouting":null}}""",
                          limits: """{"jsonrpc":"2.0","id":3,"error":{"code":-32603,"message":"not signed in"}}""");

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.IdentityError).IsNull();
        await Assert.That(result.SignIn).IsEqualTo(SignInStates.SignedOut);
        await Assert.That(result.Identity).IsNull();
        await Assert.That(result.LimitsError).IsNull();
        await Assert.That(result.LimitsNote).IsEqualTo("not signed in");
        await Assert.That(File.ReadAllText(Log)).DoesNotContain("account/rateLimits/read");
    }

    [Test]
    public async Task Api_key_account_skips_the_limits_read() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        var probe = Probe(account: """{"jsonrpc":"2.0","id":2,"result":{"account":{"type":"apiKey"},"requiresOpenaiAuth":true,"workspaceRouting":null}}""",
                          limits: """{"jsonrpc":"2.0","id":3,"error":{"code":-32603,"message":"chatgpt authentication required"}}""");

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.IdentityError).IsNull();
        await Assert.That(result.SignIn).IsEqualTo(SignInStates.SignedIn);
        await Assert.That(result.LimitsError).IsNull();
        await Assert.That(result.Limits).IsNull();
        await Assert.That(result.LimitsNote).IsEqualTo("no ChatGPT account");
        await Assert.That(File.ReadAllText(Log)).DoesNotContain("account/rateLimits/read");
    }

    [Test]
    public async Task Account_read_success_with_limits_failure_keeps_the_identity() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        var probe = Probe(limits: """{"jsonrpc":"2.0","id":3,"error":{"code":-32603,"message":"usage backend unavailable"}}""");

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.IdentityError).IsNull();
        await Assert.That(result.Identity!.Email).IsEqualTo("a@example.com");
        await Assert.That(result.Identity.VendorAccountId).IsEqualTo("acct-1");
        await Assert.That(result.Limits).IsNull();
        await Assert.That(result.LimitsError).Contains("usage backend unavailable");
    }

    [Test]
    public async Task Vendor_error_text_is_capped() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        var message = new string('x', 1000);
        var probe   = Probe(limits: $$$"""{"jsonrpc":"2.0","id":3,"error":{"code":-32603,"message":"{{{message}}}"}}""");

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.LimitsError).IsEqualTo($"account/rateLimits/read: {new string('x', 200)} (-32603)");
    }

    [Test]
    public async Task Account_id_falls_back_to_the_usage_read_without_workspace_routing() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        var probe = Probe(account: """{"jsonrpc":"2.0","id":2,"result":{"account":{"type":"chatgpt","email":null,"planType":"pro"},"requiresOpenaiAuth":true}}""");

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.Identity!.VendorAccountId).IsEqualTo("acct-1");
        await Assert.That(result.Limits).IsNotNull();
    }

    [Test]
    public async Task Disagreeing_usage_identity_drops_the_limits_and_keeps_the_account_read_id() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        var probe = Probe(limits: RateLimits.Replace("\"accountId\":\"acct-1\"", "\"accountId\":\"acct-other\"", StringComparison.Ordinal));

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.IdentityError).IsNull();
        await Assert.That(result.Identity!.VendorAccountId).IsEqualTo("acct-1");
        await Assert.That(result.Limits).IsNull();
        await Assert.That(result.LimitsError).IsEqualTo("usage identity does not match the signed-in account");
    }

    [Test]
    public async Task Unsupported_rate_limits_method_is_a_limits_error() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        var probe = Probe(limits: """{"jsonrpc":"2.0","id":3,"error":{"code":-32601,"message":"Method not found"}}""");

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.IdentityError).IsNull();
        await Assert.That(result.SignIn).IsEqualTo(SignInStates.SignedIn);
        await Assert.That(result.LimitsError).Contains("account/rateLimits/read");
    }

    [Test]
    public async Task Account_read_error_leaves_the_limits_result() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        var probe = Probe(account: """{"jsonrpc":"2.0","id":2,"error":{"code":-32603,"message":"auth store unreadable"}}""");

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.IdentityError).Contains("auth store unreadable");
        await Assert.That(result.SignIn).IsNull();
        await Assert.That(result.Identity).IsNull();
        await Assert.That(result.LimitsError).IsNull();
        await Assert.That(result.Limits).IsNotNull();
    }

    [Test]
    public async Task Server_request_mid_stream_is_answered_with_an_error() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        var probe = Probe(onAccountRead: $$$"""
            printf '%s\n' '{"jsonrpc":"2.0","id":"srv-1","method":"item/tool/requestUserInput","params":{}}'; IFS= read -r reply; printf 'REPLY %s\n' "$reply" >> '{{{Log}}}'; printf '%s\n' '{{{ChatGptAccount}}}'
            """);

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.IdentityError).IsNull();
        await Assert.That(result.Identity!.Email).IsEqualTo("a@example.com");
        await Assert.That(result.LimitsError).IsNull();
        var reply = File.ReadAllLines(Log).Single(l => l.StartsWith("REPLY ", StringComparison.Ordinal));
        await Assert.That(reply).Contains("\"id\":\"srv-1\"").And.Contains("\"error\"");
    }

    [Test]
    public async Task No_reply_within_the_deadline_fails_both_steps() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        Tmp.CreateExecutable("bin/codex", """
            #!/bin/sh
            while IFS= read -r line; do :; done
            """);
        var probe = new CodexAccountProbe(TestHarnesses.Under(Home, BinaryProbe.Searching(Tmp.PathTo("bin"))), Home, TimeProvider.System) {
            Deadline = TimeSpan.FromMilliseconds(500),
        };

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.IdentityError).IsNotNull();
        await Assert.That(result.LimitsError).IsNotNull();
        await Assert.That(result.SignIn).IsNull();
        await Assert.That(result.Limits).IsNull();
    }

    [Test]
    public async Task Missing_codex_fails_both_steps() {
        var probe = new CodexAccountProbe(TestHarnesses.Under(Home), Home, TimeProvider.System);

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.IdentityError).Contains("codex");
        await Assert.That(result.LimitsError).Contains("codex");
    }

    string ConfigToml => Home.PathTo(".codex-work/config.toml");

    [Test]
    public async Task Trusted_hooks_report_trusted_with_the_fingerprint() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        Directory.CreateDirectory(Home.PathTo(".codex-work"));
        File.WriteAllText(ConfigToml, "model = \"x\"\n");

        var result = await Probe(hooksList: HooksResult("trusted", Critical)).ProbeAsync(Account(), CancellationToken.None);

        var fingerprint = CodexTrustFingerprint.Of(AccountLayouts.Codex(Home, Account().Directory));
        await Assert.That(fingerprint).IsNotNull();
        await Assert.That(result.HookTrust).IsEqualTo(new HookTrustObservation(HookTrustStates.Trusted, fingerprint!));
        await Assert.That(File.ReadAllText(Log)).Contains("\"method\":\"hooks/list\"").And.Contains("\"cwds\"");
    }

    [Test]
    public async Task Untrusted_hooks_report_untrusted() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");

        var result = await Probe(hooksList: HooksResult("untrusted", Critical)).ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.HookTrust!.Trust).IsEqualTo(HookTrustStates.Untrusted);
    }

    [Test]
    public async Task Missing_critical_hook_reports_untrusted() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");

        var result = await Probe(hooksList: HooksResult("trusted", "sessionStart", "stop")).ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.HookTrust!.Trust).IsEqualTo(HookTrustStates.Untrusted);
    }

    [Test]
    public async Task Unsupported_hooks_list_is_no_answer() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");

        var result = await Probe().ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.HookTrust).IsNull();
        await Assert.That(result.IdentityError).IsNull();
        await Assert.That(result.LimitsError).IsNull();
        await Assert.That(result.Limits).IsNotNull();
    }

    [Test]
    public async Task Hooks_list_timeout_is_no_answer_and_keeps_identity_and_limits() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        var probe = Probe(hooksList: ":", deadline: TimeSpan.FromSeconds(2));

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.HookTrust).IsNull();
        await Assert.That(result.IdentityError).IsNull();
        await Assert.That(result.LimitsError).IsNull();
        await Assert.That(result.Identity!.Email).IsEqualTo("a@example.com");
        await Assert.That(result.Limits).IsNotNull();
    }

    [Test]
    public async Task Malformed_hooks_list_is_no_answer() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");

        var result = await Probe(hooksList: Answer("\"result\":\"nope\"")).ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.HookTrust).IsNull();
    }

    [Test]
    public async Task A_file_edited_during_the_probe_is_no_answer() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        Directory.CreateDirectory(Home.PathTo(".codex-work"));
        File.WriteAllText(ConfigToml, "model = \"x\"\n");
        var probe = Probe(hooksList: $"printf 'edit\\n' >> '{ConfigToml}'; " + HooksResult("trusted", Critical));

        var result = await probe.ProbeAsync(Account(), CancellationToken.None);

        await Assert.That(result.HookTrust).IsNull();
    }
}
