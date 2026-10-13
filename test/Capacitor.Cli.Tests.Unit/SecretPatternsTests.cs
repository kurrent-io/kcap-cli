using System.Text;
using System.Text.RegularExpressions;
using Capacitor.Cli.Capture;

namespace Capacitor.Cli.Tests.Unit;

public partial class SecretPatternsTests {
    // Reference shapes: plain left-to-right scans whose results the reshaped patterns must reproduce.
    const string UnanchoredVendorToken =
        @"(?:ghp_|gho_|ghs_|github_pat_|cfat_|(?<![A-Za-z0-9])sk-(?:proj-|live_|test_)?|sk_live_|sk_test_|xoxb-|xoxp-|xoxa-|pypi-|npm_|glpat-|dckr_pat_|dckr_oat_)[A-Za-z0-9\-_]{10,}";

    const string NameFirstEnvVarSecret =
        @"(?<![A-Za-z_])([A-Z_]*(?:SECRETS?|TOKENS?|PASSWORDS?|PASSWD|PWD|API_?KEYS?|PRIVATE_?KEYS?|CREDENTIALS?|CLIENT_?SECRETS?|ACCESS_?KEYS?|AUTH_?TOKENS?)[A-Z_]*=)([^\s""\\]+)";

    static readonly string[] Fragments = [
        "ghp_", "gho_", "ghs_", "github_pat_", "cfat_", "sk-", "sk-proj-", "sk_live_", "sk_test_", "xoxb-", "pypi-",
        "npm_", "glpat-", "dckr_pat_", "dckr_oat_", "gh", "sk", "ask-", "xox", "dckr_", "github_",
        "TOKEN", "token", "Tokens", "SECRET", "Password", "passwd", "PWD", "API_KEY", "apikey", "Private_Key",
        "CREDENTIAL", "client_secret", "ACCESS_KEYS", "auth_token", "OAUTH2", "TOKEN",
        "=", "=", " ", "\t", "\n", "\"", "\\", "_", "-", ".", ":", "a", "Z", "9", "\u212A", "\u0130", "abcdefghij", "0123456789"
    ];

    [GeneratedRegex(UnanchoredVendorToken)]
    private static partial Regex GeneratedVendorToken();

    [GeneratedRegex(NameFirstEnvVarSecret, RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedEnvVarSecret();

    // The watcher's own instances carry a wall-clock deadline that a starved runner can trip on
    // a harmless input, so the comparison runs the same generated patterns without one.
    [GeneratedRegex(SecretRedactor.VendorTokenPattern)]
    private static partial Regex UntimedWatcherVendorToken();

    [GeneratedRegex(SecretRedactor.EnvVarSecretPattern, RegexOptions.IgnoreCase)]
    private static partial Regex UntimedWatcherEnvVarSecret();

    static SecretPatterns UntimedWatcherPatterns() {
        var watcher = SecretRedactor.WatcherPatterns;
        return new() {
            SecretKeyNameRegex       = watcher.SecretKeyNameRegex,
            PemBlockRegex            = watcher.PemBlockRegex,
            AwsUniqueIdRegex         = watcher.AwsUniqueIdRegex,
            VendorTokenRegex         = UntimedWatcherVendorToken(),
            JsonKeySecretRegex       = watcher.JsonKeySecretRegex,
            EnvVarSecretRegex        = UntimedWatcherEnvVarSecret(),
            YamlStyleSecretRegex     = watcher.YamlStyleSecretRegex,
            ConnectionStringPwdRegex = watcher.ConnectionStringPwdRegex,
            AuthHeaderRegex          = watcher.AuthHeaderRegex,
            LabeledSecretRegex       = watcher.LabeledSecretRegex,
            UrlQuerySecretRegex      = watcher.UrlQuerySecretRegex,
            UrlUserinfoRegex         = watcher.UrlUserinfoRegex
        };
    }

    [Test]
    public async Task UntimedWatcherPatterns_AreTheWatcherPatterns_WithoutADeadline() {
        (Regex Untimed, Regex Watcher)[] pairs = [
            (UntimedWatcherVendorToken(), SecretRedactor.WatcherPatterns.VendorTokenRegex),
            (UntimedWatcherEnvVarSecret(), SecretRedactor.WatcherPatterns.EnvVarSecretRegex)
        ];

        foreach (var (untimed, watcher) in pairs) {
            await Assert.That(untimed.ToString()).IsEqualTo(watcher.ToString());
            await Assert.That(untimed.Options).IsEqualTo(watcher.Options);
            await Assert.That(untimed.MatchTimeout).IsEqualTo(Regex.InfiniteMatchTimeout);
        }
    }

    // Generated code and the runtime engine disagree on IgnoreCase sets (U+0130 is in [A-Z] only at
    // runtime), so each reshaped set is compared against the old shapes built the same way.
    [Test]
    public async Task ReshapedWatcherPatterns_RedactWhatTheUnanchoredScansDid() =>
        await Assert.That(Diverging(UntimedWatcherPatterns(), GeneratedVendorToken(), GeneratedEnvVarSecret())).IsEmpty();

    [Test]
    public async Task ReshapedRebuiltPatterns_RedactWhatTheUnanchoredScansDid() =>
        await Assert.That(Diverging(
            SecretRedactor.WatcherPatterns.WithMatchTimeout(Regex.InfiniteMatchTimeout),
            new Regex(UnanchoredVendorToken, RegexOptions.Compiled),
            new Regex(NameFirstEnvVarSecret, RegexOptions.IgnoreCase | RegexOptions.Compiled))).IsEmpty();

    static List<string> Diverging(SecretPatterns patterns, Regex vendor, Regex envVar) {
        var random   = new Random(1155);
        var diverged = new List<string>();

        for (var i = 0; i < 20_000; i++) {
            var text = new StringBuilder();
            for (var n = random.Next(40); n > 0; n--) text.Append(Fragments[random.Next(Fragments.Length)]);
            var value = text.ToString();

            if (patterns.ReplaceVendorTokens(value, "[R]", RedactionBudget.Unlimited) != vendor.Replace(value, "[R]")
             || patterns.EnvVarSecretRegex.Replace(value, "=[R]") != envVar.Replace(value, "$1[R]"))
                diverged.Add(value);
        }

        return diverged;
    }

    [Test]
    [Arguments("ghp_abcdefghij0123", "[REDACTED]")]
    [Arguments("x ghp_abcdefghij", "x [REDACTED]")]
    [Arguments("x ghp_abcdefghi", null)]
    [Arguments("ghp_ghp_abcdefghij", "[REDACTED]")]
    [Arguments("xgho_abcdefghijkl", "x[REDACTED]")]
    [Arguments("sk-abcdefghijklmnop", "[REDACTED]")]
    [Arguments("a-sk-abcdefghijklmnop", "a-[REDACTED]")]
    [Arguments("ask-abcdefghijklmnop", null)]
    [Arguments("1sk-abcdefghijklmnop", null)]
    [Arguments("tasksk-abcdefghijklmnop", null)]
    [Arguments("ask-x sk_live_abcdefghij", "ask-x [REDACTED]")]
    public async Task VendorToken_RedactsFromItsPrefixToTheEndOfTheRun(string value, string? expected) {
        await Assert.That(SecretRedactor.RedactValue(value, keyIsSecret: false)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("OAUTH2_TOKEN=abc", "OAUTH2_TOKEN=[REDACTED]")]
    [Arguments("Db_Password=hunter2", "Db_Password=[REDACTED]")]
    [Arguments("export my_api_key=abc def", "export my_api_key=[REDACTED] def")]
    [Arguments("a.TOKEN_COUNT=5", "a.TOKEN_COUNT=[REDACTED]")]
    [Arguments("PWD=a=TOKEN=b", "PWD=[REDACTED]")]
    [Arguments("x=1 SECRET=s", "x=1 SECRET=[REDACTED]")]
    [Arguments("TOKEN=", null)]
    [Arguments("=abc", null)]
    [Arguments("TOK=abc", null)]
    [Arguments("TOKEN2=abc", null)]
    public async Task EnvVarSecret_KeepsTheNameAndRedactsTheValue(string value, string? expected) {
        await Assert.That(SecretRedactor.RedactValue(value, keyIsSecret: false)).IsEqualTo(expected);
    }
}
