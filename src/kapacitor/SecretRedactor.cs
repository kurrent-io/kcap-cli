using System.Text.Json;
using System.Text.RegularExpressions;

namespace kapacitor;

static partial class SecretRedactor {
    public static string RedactLine(string rawJsonlLine) {
        try {
            using var doc  = JsonDocument.Parse(rawJsonlLine);
            var       root = doc.RootElement;

            // Resolve the user message element from either format:
            //   Direct format:   { "type": "user", "message": { "role": "user", "content": [...] } }
            //   Progress format: { "type": "progress", "data": { "message": { "type": "user", "message": { ... } } } }
            JsonElement? userMessage;

            if (root.Str("type") == "user") {
                // Direct format — the root IS the user event; content lives at root.message.content
                userMessage = root;
            } else {
                // Progress wrapper — content lives at data.message.message.content
                var nested = root.Obj("data")?.Obj("message");

                if (nested is null || nested.Value.Str("type") != "user")
                    return rawJsonlLine;

                userMessage = nested;
            }

            var content = userMessage.Value.Obj("message")?.Arr("content");

            if (content is null)
                return rawJsonlLine;

            var hasToolResult = false;

            foreach (var block in content.Value.EnumerateArray()) {
                if (block.Str("type") == "tool_result") {
                    hasToolResult = true;

                    break;
                }
            }

            if (!hasToolResult)
                return rawJsonlLine;

            // We have tool_result blocks — redact the line as a raw string.
            // Working on the serialized JSON string lets us handle both the
            // content field and toolUseResult without manual tree rewriting.
            var redacted = RedactSecrets(rawJsonlLine);

            return redacted == rawJsonlLine ? rawJsonlLine : redacted;
        } catch {
            // Malformed JSON — pass through unchanged
            return rawJsonlLine;
        }
    }

    static string RedactSecrets(string text) {
        text = PemBlockRegex.Replace(text, "[REDACTED]");
        text = AwsUniqueIdRegex.Replace(text, "[REDACTED]");
        text = VendorTokenRegex.Replace(text, "[REDACTED]");
        text = AuthHeaderRegex.Replace(text, "$1[REDACTED]");
        text = JsonKeySecretRegex.Replace(text, "$1[REDACTED]$3");
        text = EnvVarSecretRegex.Replace(text, "$1[REDACTED]");
        text = YamlStyleSecretRegex.Replace(text, "$1[REDACTED]");
        text = LabeledSecretRegex.Replace(text, "$1[REDACTED]");
        text = ConnectionStringPwdRegex.Replace(text, "$1[REDACTED]$3");

        return text;
    }

    // Matches PEM private key blocks (handles both real newlines and \\n escaped newlines in JSON strings)
    [GeneratedRegex(@"-----BEGIN[A-Z\s]*PRIVATE KEY-----(?:\\n|[\s\S])*?-----END[A-Z\s]*PRIVATE KEY-----", RegexOptions.None)]
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
    // Each prefix is specific enough to avoid false positives
    [GeneratedRegex(@"(?:ghp_|gho_|ghs_|github_pat_|cfat_|sk-(?:proj-|live_|test_)?|sk_live_|sk_test_|xoxb-|xoxp-|xoxa-|pypi-|npm_|glpat-)[A-Za-z0-9\-_]{10,}", RegexOptions.None)]
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
    [GeneratedRegex(@"((?:secret|token|password|passwd|pwd|api_key|apikey|private_key|credentials|client_secret|access_key|auth_token)[^:\n]*:[ \t]+)([^\s""\\]{8,})", RegexOptions.IgnoreCase)]
    private static partial Regex YamlStyleSecretRx();

    static readonly Regex YamlStyleSecretRegex = YamlStyleSecretRx();

    // Connection string: Password=value; or Pwd=value;
    // Excludes " and \ to avoid crossing JSON string boundaries
    // group 1 = key=, group 2 = value, group 3 = ; or end
    [GeneratedRegex(@"((?:Password|Pwd)\s*=\s*)([^;""\\]+)(;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringPwdRx();

    static readonly Regex ConnectionStringPwdRegex = ConnectionStringPwdRx();

    // HTTP auth-bearing headers — redact the entire header value.
    // Covers Authorization (Bearer/Basic/Digest/etc.), Cookie, Set-Cookie, and common API-key
    // header variants. Tolerates JSON-escaped quotes around the key and value, so it matches both
    // raw `Authorization: Bearer xxx` (e.g. inside a curl tool_result) and `\"Authorization\": \"Bearer xxx\"`
    // (HTTP request logged as a JSON object). Excludes " and \ to stop at JSON string boundaries.
    // group 1 = header name + colon + optional opening quote, group 2 = value
    [GeneratedRegex(
        """((?:authorization|proxy-authorization|cookie|set-cookie|x-api-key|x-auth-token|x-access-token|x-amz-security-token|x-goog-api-key|api-key)(?:\\")?\s*:\s*(?:\\")?\s*)([^\r\n"\\]+)""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex AuthHeaderRx();

    static readonly Regex AuthHeaderRegex = AuthHeaderRx();

    // Labeled secret — secret keyword followed by whitespace (NOT a colon) and an opaque value.
    // Catches `hcloud:token  9xKMA…` and similar where the keyword is a label, not a key:value
    // separator. The colon form is already covered by YamlStyleSecretRegex.
    // 16-char minimum + character set [A-Za-z0-9_\-+/=.] keeps this from matching prose like
    // "the token might fail" while still catching base64/base62/url-safe API tokens.
    // group 1 = keyword + whitespace, group 2 = value
    [GeneratedRegex(
        @"\b((?:secret|token|password|passwd|pwd|api_key|apikey|private_key|credentials|client_secret|access_key|auth_token)\b[ \t]+)([A-Za-z0-9_\-+/=.]{16,})",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex LabeledSecretRx();

    static readonly Regex LabeledSecretRegex = LabeledSecretRx();
}
