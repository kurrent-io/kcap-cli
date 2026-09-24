using System.Buffers;
using System.Text.RegularExpressions;
using Capacitor.Cli.Capture;
using static Capacitor.Cli.SecretRedactor;

namespace Capacitor.Cli;

// The value-level pipeline over one vocabulary, so the same patterns run under the watcher's
// per-call deadline or a longer one.
sealed class SecretPatterns {
    public required Regex SecretKeyNameRegex { get; init; }
    public required Regex PemBlockRegex { get; init; }
    public required Regex AwsUniqueIdRegex { get; init; }
    public required Regex VendorTokenRegex { get; init; }
    public required Regex JsonKeySecretRegex { get; init; }
    public required Regex EnvVarSecretRegex { get; init; }
    public required Regex YamlStyleSecretRegex { get; init; }
    public required Regex ConnectionStringPwdRegex { get; init; }
    public required Regex AuthHeaderRegex { get; init; }
    public required Regex LabeledSecretRegex { get; init; }
    public required Regex UrlQuerySecretRegex { get; init; }
    public required Regex UrlUserinfoRegex { get; init; }

    public SecretPatterns WithMatchTimeout(TimeSpan timeout) => new() {
        SecretKeyNameRegex       = Rebuild(SecretKeyNameRegex, timeout),
        PemBlockRegex            = Rebuild(PemBlockRegex, timeout),
        AwsUniqueIdRegex         = Rebuild(AwsUniqueIdRegex, timeout),
        VendorTokenRegex         = Rebuild(VendorTokenRegex, timeout),
        JsonKeySecretRegex       = Rebuild(JsonKeySecretRegex, timeout),
        EnvVarSecretRegex        = Rebuild(EnvVarSecretRegex, timeout),
        YamlStyleSecretRegex     = Rebuild(YamlStyleSecretRegex, timeout),
        ConnectionStringPwdRegex = Rebuild(ConnectionStringPwdRegex, timeout),
        AuthHeaderRegex          = Rebuild(AuthHeaderRegex, timeout),
        LabeledSecretRegex       = Rebuild(LabeledSecretRegex, timeout),
        UrlQuerySecretRegex      = Rebuild(UrlQuerySecretRegex, timeout),
        UrlUserinfoRegex         = Rebuild(UrlUserinfoRegex, timeout)
    };

    // Compiled is ignored under NativeAOT, where a rebuilt set is never made; elsewhere the
    // interpreter would scan a recording several times slower than the generated code does.
    static Regex Rebuild(Regex generated, TimeSpan timeout) =>
        new(generated.ToString(), generated.Options | RegexOptions.Compiled, timeout);

    public bool IsSecretKey(ReadOnlySpan<char> propertyName, RedactionBudget budget) =>
        Matches(SecretKeyNameRegex, propertyName, budget);

    public string? Redact(ReadOnlySpan<char> value, bool keyIsSecret, RedactionBudget budget) {
        if (keyIsSecret) return value.SequenceEqual(RedactedMarker) ? null : RedactedMarker;
        if (!AnyPatternMatches(value, budget)) return null;

        var text     = new string(value);
        var redacted = RedactSecrets(text, budget);

        return string.Equals(redacted, text, StringComparison.Ordinal) ? null : redacted;
    }

    const int ShortestMatchableCredential = 13;

    public bool IsSecretItself(ReadOnlySpan<char> value, RedactionBudget budget) =>
        value.Length >= ShortestMatchableCredential
     && (Matches(VendorTokenRegex, value, budget) || Matches(AwsUniqueIdRegex, value, budget) || Matches(PemBlockRegex, value, budget));

    bool AnyPatternMatches(ReadOnlySpan<char> value, RedactionBudget budget) =>
        Matches(PemBlockRegex, value, budget)
     || Matches(AwsUniqueIdRegex, value, budget)
     || Matches(VendorTokenRegex, value, budget)
     || Matches(AuthHeaderRegex, value, budget)
     || Matches(UrlQuerySecretRegex, value, budget)
     || Matches(UrlUserinfoRegex, value, budget)
     || Matches(JsonKeySecretRegex, value, budget)
     || Matches(EnvVarSecretRegex, value, budget)
     || Matches(YamlStyleSecretRegex, value, budget)
     || Matches(LabeledSecretRegex, value, budget)
     || Matches(ConnectionStringPwdRegex, value, budget);

    public string RedactSecrets(string text, RedactionBudget budget) {
        text = Replace(PemBlockRegex, text, RedactedMarker, budget);
        text = Replace(AwsUniqueIdRegex, text, RedactedMarker, budget);
        text = Replace(VendorTokenRegex, text, RedactedMarker, budget);
        text = Replace(AuthHeaderRegex, text, "$1" + RedactedMarker, budget);
        text = Replace(UrlQuerySecretRegex, text, "$1" + RedactedMarker, budget);
        text = Replace(UrlUserinfoRegex, text, "$1" + RedactedMarker + "$3", budget);
        text = Replace(JsonKeySecretRegex, text, "$1" + RedactedMarker + "$3", budget);
        text = Replace(EnvVarSecretRegex, text, "$1" + RedactedMarker, budget);
        text = Replace(YamlStyleSecretRegex, text, "$1" + RedactedMarker, budget);
        text = Replace(LabeledSecretRegex, text, "$1" + RedactedMarker, budget);
        text = Replace(ConnectionStringPwdRegex, text, "$1" + RedactedMarker + "$3", budget);

        return text;
    }

    bool Matches(Regex regex, ReadOnlySpan<char> value, RedactionBudget budget) {
        budget.Check();
        if (!CanMatch(regex, value)) return false;
        return regex.IsMatch(value);
    }

    string Replace(Regex regex, string value, string replacement, RedactionBudget budget) {
        budget.Check();
        if (!CanMatch(regex, value)) return value;
        return regex.Replace(value, replacement);
    }

    // These delimiters are mandatory in the patterns, so skipping their absence is exact.
    bool CanMatch(Regex regex, ReadOnlySpan<char> value) {
        if (regex == VendorTokenRegex) return value.ContainsAny(VendorPrefixes);
        if (regex == EnvVarSecretRegex || regex == ConnectionStringPwdRegex || regex == UrlQuerySecretRegex)
            return value.Contains('=');
        if (regex == AuthHeaderRegex || regex == JsonKeySecretRegex || regex == YamlStyleSecretRegex)
            return value.Contains(':');
        if (regex == UrlUserinfoRegex) return value.Contains('@');
        if (regex == LabeledSecretRegex) return value.IndexOfAny(' ', '\t') >= 0;
        return true;
    }

    static readonly SearchValues<string> VendorPrefixes = SearchValues.Create(
        ["ghp_", "gho_", "ghs_", "github_pat_", "cfat_", "sk-", "sk_live_", "sk_test_", "xoxb-", "xoxp-", "xoxa-",
         "pypi-", "npm_", "glpat-", "dckr_pat_", "dckr_oat_"], StringComparison.Ordinal);
}
