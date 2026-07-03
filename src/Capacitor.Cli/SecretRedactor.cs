using System.Text.RegularExpressions;

namespace Capacitor.Cli;

static partial class SecretRedactor {
    // Above this length, lines are replaced with an opaque placeholder instead of being scanned
    // by the regex pipeline. Real conversation turns and small tool results stay well under this;
    // lines above it are almost always truncated dumps (mid-key secret blobs, base64 blobs) that
    // would trip regex alternation paths into catastrophic backtracking (see AI-783 / line 953
    // incident where an unterminated `-----BEGIN RSA PRIVATE KEY-----` blob wedged the watcher
    // main loop at 100% CPU). UTF-16 code units, not bytes — the redaction cost is dominated by
    // regex stepping over chars, and the limit is a coarse defense-in-depth bound, not a wire-size
    // budget.
    internal const int MaxRedactableLineChars = 64 * 1024;

    // Sent in place of an oversize line so its content never reaches the server even partially
    // redacted. The server treats unknown top-level `type` values as a no-op event, which keeps
    // line numbering stable for resume/gap-recovery while guaranteeing no raw secret bytes leave
    // the host.
    internal const string OversizeLinePlaceholder =
        """{"type":"redacted_oversize_line","reason":"line exceeded SecretRedactor size limit"}""";

    public static string RedactLine(string rawJsonlLine) {
        if (rawJsonlLine.Length > MaxRedactableLineChars) return OversizeLinePlaceholder;

        // Scan every line, regardless of message type. The pipeline used to gate on
        // `user`+`tool_result` on the assumption that secrets only enter the transcript via tool
        // output, but the assistant routinely paraphrases tool output back into its own narrative
        // (re-emitting tokens that were already redacted upstream), and human user turns can paste
        // tokens too. Working on the serialized JSON string handles both `content` and
        // `toolUseResult` without manual tree rewriting; the patterns are constructed to be safe
        // against JSON delimiters (excluding `"` and `\` where needed).
        return RedactSecrets(rawJsonlLine);
    }

    static string RedactSecrets(string text) {
        text = PemBlockRegex.Replace(text, "[REDACTED]");
        text = AwsUniqueIdRegex.Replace(text, "[REDACTED]");
        text = VendorTokenRegex.Replace(text, "[REDACTED]");
        text = AuthHeaderRegex.Replace(text, "$1[REDACTED]");
        text = UrlQuerySecretRegex.Replace(text, "$1[REDACTED]");
        text = UrlUserinfoRegex.Replace(text, "$1[REDACTED]$3");
        text = JsonKeySecretRegex.Replace(text, "$1[REDACTED]$3");
        text = EnvVarSecretRegex.Replace(text, "$1[REDACTED]");
        text = YamlStyleSecretRegex.Replace(text, "$1[REDACTED]");
        text = LabeledSecretRegex.Replace(text, "$1[REDACTED]");
        text = ConnectionStringPwdRegex.Replace(text, "$1[REDACTED]$3");

        return text;
    }

    // Matches PEM private key blocks. `[\s\S]` already covers `\` and `n` individually, so the
    // earlier `(?:\\n|[\s\S])` alternation was redundant — and catastrophically backtrackable when
    // the BEGIN marker appears without a matching END (e.g. truncated tool dumps). The `{0,16384}`
    // upper bound caps the search even if a future regex change reintroduces ambiguity; real PEM
    // keys (RSA-4096 armored) are ~3.2KB so this leaves plenty of headroom.
    [GeneratedRegex(@"-----BEGIN[A-Z\s]*PRIVATE KEY-----[\s\S]{0,16384}?-----END[A-Z\s]*PRIVATE KEY-----", RegexOptions.None)]
    private static partial Regex PemBlockRx();

    static readonly Regex PemBlockRegex = PemBlockRx();

    // AWS unique ID prefixes (access keys, session tokens, IAM principals).
    // See: https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_identifiers.html#identifiers-prefixes
    // Access keys (AKIA/ASIA) are 20 chars total; IAM principal unique IDs are typically 21 chars
    // but not strictly length-bounded, so match {16,128} with a non-alnum lookahead to avoid
    // leaving a trailing character adjacent to [REDACTED].
    [GeneratedRegex("(?:AKIA|ASIA|AROA|AIDA|AIPA|AGPA|ANPA|ANVA|ASCA|APKA|ABIA|ACCA)[0-9A-Z]{16,128}(?![0-9A-Z])", RegexOptions.None)]
    private static partial Regex AwsUniqueIdRx();

    static readonly Regex AwsUniqueIdRegex = AwsUniqueIdRx();

    // Known vendor token prefixes followed by token characters
    // Each prefix is specific enough to avoid false positives — EXCEPT the bare `sk-` (OpenAI)
    // prefix, which is short enough to appear mid-word: it matched the `sk-notification` substring
    // inside "ta·sk-notification", redacting Claude Code's injected background-task blocks to
    // `<ta[REDACTED]> … </ta[REDACTED]>` (AI-1162). Gate the `sk-` branch on a non-alphanumeric
    // lookbehind so it only fires at a token boundary; real keys (`sk-proj-…`, `sk-live_…`,
    // preceded by whitespace/quote/punctuation) still redact, while `disk-`, `task-`, `kiosk-` etc.
    // pass through. The other prefixes carry `_`/distinctive spellings and don't collide mid-word.
    [GeneratedRegex(@"(?:ghp_|gho_|ghs_|github_pat_|cfat_|(?<![A-Za-z0-9])sk-(?:proj-|live_|test_)?|sk_live_|sk_test_|xoxb-|xoxp-|xoxa-|pypi-|npm_|glpat-|dckr_pat_|dckr_oat_)[A-Za-z0-9\-_]{10,}", RegexOptions.None)]
    private static partial Regex VendorTokenRx();

    static readonly Regex VendorTokenRegex = VendorTokenRx();

    // JSON key: matches "secret_name": "value" or \"secret_name\": \"value\" (escaped quotes inside JSON strings)
    // group 1 = opening quote(s) + key name + closing quote(s) + colon + space + opening quote(s)
    // group 2 = value
    // group 3 = closing quote(s)
    [GeneratedRegex(
        """((?:\\"|")(?:[^"\\]*(?:secret|token|password|passwd|pwd|api_key|apikey|private_key|credentials|client_secret|access_key|auth_token)[^"\\]*)(?:\\"|")[ \t]*:[ \t]*(?:\\"|"))([^"\\]+)((?:\\"|"))""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex JsonKeySecretRx();

    static readonly Regex JsonKeySecretRegex = JsonKeySecretRx();

    // Env var: SECRET_NAME=value (uppercase key containing secret keyword, value until whitespace or JSON delimiter)
    // Excludes " and \ to avoid consuming JSON string boundaries when matching against raw serialized JSON
    [GeneratedRegex(@"([A-Z_]*(?:SECRET|TOKEN|PASSWORD|PASSWD|PWD|API_KEY|APIKEY|PRIVATE_KEY|CREDENTIALS|CLIENT_SECRET|ACCESS_KEY|AUTH_TOKEN)[A-Z_]*=)([^\s""\\]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EnvVarSecretRx();

    static readonly Regex EnvVarSecretRegex = EnvVarSecretRx();

    // YAML-style: secret_name: value (key containing secret keyword followed by colon, space, and value)
    // Excludes " and \ to avoid crossing JSON string boundaries. Minimum 8 chars to reduce false positives.
    //
    // The gap between the keyword and the colon is `[\w.\-]*` — only key-name characters — NOT the
    // former `[^:\n]*`. `[^:\n]*` matched any run of non-colon chars, so a secret keyword appearing
    // anywhere earlier in a prose sentence reached across to an unrelated prose colon and redacted
    // whatever 8+-char word followed it: `"...access token. The one real risk: model-id matching"`
    // had `model-id` (exactly 8 chars) replaced with [REDACTED]. Constraining the gap to identifier
    // characters forces the keyword to actually be part of the `key:` token, while still allowing
    // real keys like `client-secret:`, `aws.secret.access.key:`, and `auth_token:`.
    [GeneratedRegex(@"((?:secret|token|password|passwd|pwd|api_key|apikey|private_key|credentials|client_secret|access_key|auth_token)[\w.\-]*:[ \t]+)([^\s""\\]{8,})", RegexOptions.IgnoreCase)]
    private static partial Regex YamlStyleSecretRx();

    static readonly Regex YamlStyleSecretRegex = YamlStyleSecretRx();

    // Connection string: Password=value; or Pwd=value;
    // Excludes " and \ to avoid crossing JSON string boundaries
    // group 1 = key=, group 2 = value, group 3 = ; or end
    [GeneratedRegex(@"((?:Password|Pwd)\s*=\s*)([^;""\\]+)(;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringPwdRx();

    static readonly Regex ConnectionStringPwdRegex = ConnectionStringPwdRx();

    // HTTP auth-bearing headers — redact the entire header value.
    // Covers Authorization (Bearer/Basic/Digest/etc.), Cookie, Set-Cookie, CSRF tokens, GitLab
    // tokens, signature headers, and common API-key header variants. `(?:\\?")?` matches both real
    // JSON-object form (`"Authorization":"…"`) and the JSON-escaped form found inside a
    // serialized tool_result content string (`\"Authorization\": \"…\"`).
    //
    // Known limitation: header values that embed escaped quotes (e.g. `Set-Cookie: a=\"b\"; …`
    // inside JSON-encoded content) capture only up to the first `\`. Fixing this properly
    // requires JSON-tree-aware redaction (parse → walk strings → redact decoded → re-serialize),
    // tracked as a follow-up — see AI-649.
    //
    // group 1 = header name + colon + optional opening quote, group 2 = value
    [GeneratedRegex(
        """((?:authorization|proxy-authorization|cookie|set-cookie|x-api-key|x-auth-token|x-access-token|x-amz-security-token|x-amz-signature|x-goog-api-key|api-key|private-token|job-token|deploy-token|x-vault-token|x-consul-token|x-csrf-token|x-xsrf-token|x-hub-signature(?:-256)?|x-slack-signature|stripe-signature|x-registry-auth)(?:\\?")?\s*:\s*(?:\\?")?\s*)([^\r\n"\\]+)""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex AuthHeaderRx();

    static readonly Regex AuthHeaderRegex = AuthHeaderRx();

    // Labeled secret — secret keyword followed by whitespace (NOT a colon) and an opaque value.
    // Catches `hcloud:token  9xKMA…` and similar where the keyword is a label, not a key:value
    // separator. The colon form is already covered by YamlStyleSecretRegex.
    // 16-char minimum on `[^\s"\\]` value covers tokens with punctuation (e.g. `password p@ss!w0rd…`)
    // while the 16-char floor keeps prose like "the token might fail" from matching.
    // group 1 = keyword + whitespace, group 2 = value
    [GeneratedRegex(
        """\b((?:secret|token|password|passwd|pwd|api_key|apikey|private_key|credentials|client_secret|access_key|auth_token)\b[ \t]+)([^\s"\\]{16,})""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex LabeledSecretRx();

    static readonly Regex LabeledSecretRegex = LabeledSecretRx();

    // URL query secrets — `?key=value` or `&key=value` where the param name is a known secret-bearing
    // key. Covers OAuth tokens, signed-URL signatures, AWS pre-signed URL params, and common API-key
    // query patterns. Stops at `&`, `#`, whitespace, or JSON string boundaries.
    // group 1 = `[?&]key=`, group 2 = value
    [GeneratedRegex(
        """([?&](?:access_token|refresh_token|id_token|client_secret|signature|sig|x-amz-signature|awsaccesskeyid|api_key|apikey|api-key|token|password|secret|auth_token|sas)=)([^&\s"\\#]+)""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex UrlQuerySecretRx();

    static readonly Regex UrlQuerySecretRegex = UrlQuerySecretRx();

    // URL userinfo — `https://user:password@host` form. Redacts the password component only.
    // group 1 = scheme + user + colon, group 2 = password, group 3 = @
    [GeneratedRegex(
        """(https?://[^:/\s"\\@]+:)([^@\s"\\/]+)(@)""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex UrlUserinfoRx();

    static readonly Regex UrlUserinfoRegex = UrlUserinfoRx();
}
