using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Capture;

namespace Capacitor.Cli;

public static partial class SecretRedactor {
    // Only malformed input uses this smaller textual-fallback limit.
    internal const int MaxRedactableLineChars = 64 * 1024;
    internal const int MaxRecordBytes = 4 * 1024 * 1024;

    internal const string RedactedMarker = "[REDACTED]";

    internal const string OversizeLinePlaceholder =
        """{"type":"redacted_oversize_line","reason":"line exceeded SecretRedactor size limit"}""";

    internal const string UnparsableOutputPlaceholder =
        """{"type":"redacted_unparsable_line","reason":"redacted line was no longer valid JSON"}""";

    // The generated patterns carry the watcher's per-call deadline. A caller scanning a whole
    // recording has no loop to keep responsive, so it gets the same vocabulary with none.
    internal static readonly SecretPatterns Bounded = new() {
        SecretKeyNameRegex       = SecretKeyNameRx(),
        PemBlockRegex            = PemBlockRx(),
        AwsUniqueIdRegex         = AwsUniqueIdRx(),
        VendorTokenRegex         = VendorTokenRx(),
        JsonKeySecretRegex       = JsonKeySecretRx(),
        EnvVarSecretRegex        = EnvVarSecretRx(),
        YamlStyleSecretRegex     = YamlStyleSecretRx(),
        ConnectionStringPwdRegex = ConnectionStringPwdRx(),
        AuthHeaderRegex          = AuthHeaderRx(),
        LabeledSecretRegex       = LabeledSecretRx(),
        UrlQuerySecretRegex      = UrlQuerySecretRx(),
        UrlUserinfoRegex         = UrlUserinfoRx()
    };

    internal static readonly Lazy<SecretPatterns> Unbounded = new(Bounded.WithoutMatchTimeout);

    public static string RedactLine(string rawJsonlLine) => RedactLineWithOutcome(rawJsonlLine).Line;

    public static RedactionOutcome RedactLineWithOutcome(string rawJsonlLine) =>
        RedactLineWithOutcome(rawJsonlLine, TimeProvider.System);

    internal static RedactionOutcome RedactLineWithOutcome(string rawJsonlLine, TimeProvider time) {
        var bytes = Encoding.UTF8.GetByteCount(rawJsonlLine);
        if (bytes > MaxRecordBytes) return Lost(RedactionLossReason.InputLimit);
        var budget = new RedactionBudget(time);
        try {
            string line;
            RedactionLossReason? loss = null;
            try {
                line = RedactJsonStringValues(rawJsonlLine, bytes, budget) ?? rawJsonlLine;
            } catch (JsonException) {
                if (rawJsonlLine.Length > MaxRedactableLineChars)
                    return Lost(RedactionLossReason.MalformedInput);
                line = Bounded.RedactSecrets(rawJsonlLine, budget);
                loss = RedactionLossReason.MalformedInput;
            }
            budget.Check();
            if (Encoding.UTF8.GetByteCount(line) > MaxRecordBytes) return Lost(RedactionLossReason.OutputLimit);
            return new RedactionOutcome(line, loss, rawJsonlLine.Length, bytes);
        } catch (RegexMatchTimeoutException) {
            return Lost(RedactionLossReason.RegexTimeout);
        } catch (RedactionBudgetExceededException) {
            return Lost(RedactionLossReason.RecordBudget);
        } catch (RedactionOutputLimitException) {
            return Lost(RedactionLossReason.OutputLimit);
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
            return Lost(RedactionLossReason.MalformedInput);
        }

        RedactionOutcome Lost(RedactionLossReason reason) => new(
            rawJsonlLine.Length > MaxRedactableLineChars ? OversizeLinePlaceholder : UnparsableOutputPlaceholder,
            reason, rawJsonlLine.Length, bytes);
    }

    public static bool IsSecretKey(ReadOnlySpan<char> propertyName) =>
        Unbounded.Value.IsSecretKey(propertyName, RedactionBudget.Unlimited);

    public static string? RedactValue(ReadOnlySpan<char> value, bool keyIsSecret) =>
        Unbounded.Value.Redact(value, keyIsSecret, RedactionBudget.Unlimited);

    static string? RedactJsonStringValues(string line, int byteCount, RedactionBudget budget) {
        if (line.Length == 0) return null;

        var input = ArrayPool<byte>.Shared.Rent(byteCount);
        var decoded = ArrayPool<char>.Shared.Rent(line.Length);

        try {
            Encoding.UTF8.GetBytes(line, input.AsSpan());
            if (!Rewrite(input.AsSpan(0, byteCount), decoded, null, budget)) return null;
            using var output = new BoundedJsonBufferWriter(MaxRecordBytes);
            using var writer = new Utf8JsonWriter(output, WriterOptions);
            Rewrite(input.AsSpan(0, byteCount), decoded, writer, budget);
            return Encoding.UTF8.GetString(output.WrittenMemory.Span);
        } finally {
            ArrayPool<char>.Shared.Return(decoded, clearArray: true);
            ArrayPool<byte>.Shared.Return(input, clearArray: true);
        }
    }

    // Scan first so an unchanged record keeps its original encoding and cannot exceed an output cap.
    static bool Rewrite(ReadOnlySpan<byte> utf8, Span<char> decoded, Utf8JsonWriter? writer, RedactionBudget budget) {
        var reader = new Utf8JsonReader(utf8, ReaderOptions);

        var redactedAny = false;
        var keyIsSecret = false;
        var redactedKeys = 0;

        var secretDepth = -1;

        while (reader.Read()) {
            budget.Check();
            if (reader.CurrentDepth >= 1000 && reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                throw new InvalidOperationException("JSON depth exceeds the processing limit.");
            var inSecret = secretDepth >= 0;

            switch (reader.TokenType) {
                case JsonTokenType.PropertyName:
                    var name = decoded[..reader.CopyString(decoded)];
                    keyIsSecret = inSecret || Bounded.IsSecretKey(name, budget);

                    if (Bounded.IsSecretItself(name, budget)) {
                        writer?.WritePropertyName($"{RedactedMarker}-{++redactedKeys}");
                        redactedAny = true;
                    } else {
                        writer?.WritePropertyName(JsonEncodedText.Encode(name, WriterOptions.Encoder));
                    }

                    continue;

                case JsonTokenType.String:
                    var value = decoded[..reader.CopyString(decoded)];
                    if (Bounded.Redact(value, keyIsSecret || inSecret, budget) is { } clean) {
                        writer?.WriteStringValue(JsonEncodedText.Encode(clean, WriterOptions.Encoder));
                        redactedAny = true;
                    } else {
                        writer?.WriteStringValue(JsonEncodedText.Encode(value, WriterOptions.Encoder));
                    }

                    break;

                // Token counters match secret key names, but numeric values must retain accounting.
                case JsonTokenType.Number:
                    writer?.WriteRawValue(reader.ValueSpan, skipInputValidation: true);

                    break;

                case JsonTokenType.StartObject:
                    if (keyIsSecret && !inSecret) secretDepth = reader.CurrentDepth;
                    writer?.WriteStartObject();

                    break;

                case JsonTokenType.StartArray:
                    if (keyIsSecret && !inSecret) secretDepth = reader.CurrentDepth;
                    writer?.WriteStartArray();

                    break;

                case JsonTokenType.EndObject:
                    writer?.WriteEndObject();
                    if (inSecret && reader.CurrentDepth == secretDepth) secretDepth = -1;

                    break;

                case JsonTokenType.EndArray:
                    writer?.WriteEndArray();
                    if (inSecret && reader.CurrentDepth == secretDepth) secretDepth = -1;

                    break;

                case JsonTokenType.Comment:
                    redactedAny = true;

                    continue;

                case JsonTokenType.True:  writer?.WriteBooleanValue(true); break;
                case JsonTokenType.False: writer?.WriteBooleanValue(false); break;
                case JsonTokenType.Null:  writer?.WriteNullValue(); break;

                default: throw new InvalidOperationException($"Unhandled JSON token {reader.TokenType}.");
            }

            keyIsSecret = false;
        }

        writer?.Flush();

        return redactedAny;
    }

    // One look-ahead level distinguishes excessive depth from malformed text without raw fallback.
    static readonly JsonReaderOptions ReaderOptions =
        new() { MaxDepth = 1001, CommentHandling = JsonCommentHandling.Allow };

    static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    const string SecretKeywords =
        "secrets?|tokens?|passwords?|passwd|pwd|api[-_.]?keys?|private[-_.]?keys?|credentials?|client[-_.]?secrets?|access[-_.]?keys?|auth[-_.]?tokens?";

    const string AuthHeaderNames =
        "authorization|proxy-authorization|cookie|set-cookie|x-api-key|x-auth-token|x-access-token|x-amz-security-token|x-amz-signature|x-goog-api-key|api-key|private-token|job-token|deploy-token|x-vault-token|x-consul-token|x-csrf-token|x-xsrf-token|x-hub-signature(?:-256)?|x-slack-signature|stripe-signature|x-registry-auth";

    [GeneratedRegex("(?:" + AuthHeaderNames + "|auth)$|(?:" + SecretKeywords + ")", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex SecretKeyNameRx();

    [GeneratedRegex(@"-----BEGIN[A-Z\s]*PRIVATE KEY-----[\s\S]{0,16384}?-----END[A-Z\s]*PRIVATE KEY-----", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex PemBlockRx();

    [GeneratedRegex("(?:AKIA|ASIA|AROA|AIDA|AIPA|AGPA|ANPA|ANVA|ASCA|APKA|ABIA|ACCA)[0-9A-Z]{16,128}(?![0-9A-Z])", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex AwsUniqueIdRx();

    [GeneratedRegex(@"(?:ghp_|gho_|ghs_|github_pat_|cfat_|(?<![A-Za-z0-9])sk-(?:proj-|live_|test_)?|sk_live_|sk_test_|xoxb-|xoxp-|xoxa-|pypi-|npm_|glpat-|dckr_pat_|dckr_oat_)[A-Za-z0-9\-_]{10,}", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex VendorTokenRx();

    [GeneratedRegex(
        """((?:\\"|")(?:[^"\\]*(?:""" + SecretKeywords + """)[^"\\]*)(?:\\"|")[ \t]*:[ \t]*(?:\\"|"))([^"\\]+)((?:\\"|")|$)""",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100
    )]
    private static partial Regex JsonKeySecretRx();

    // The run boundary keeps scanning linear; digits must remain eligible for OAUTH2_TOKEN.
    [GeneratedRegex(@"(?<![A-Za-z_])([A-Z_]*(?:SECRETS?|TOKENS?|PASSWORDS?|PASSWD|PWD|API_?KEYS?|PRIVATE_?KEYS?|CREDENTIALS?|CLIENT_?SECRETS?|ACCESS_?KEYS?|AUTH_?TOKENS?)[A-Z_]*=)([^\s""\\]+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex EnvVarSecretRx();

    [GeneratedRegex("""((?:""" + SecretKeywords + """)[\w.\-]*:[ \t]+)([^\s"\\]{8,})""", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex YamlStyleSecretRx();

    [GeneratedRegex(@"((?:Password|Pwd)\s*=\s*)([^;""\\]+)(;|$)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex ConnectionStringPwdRx();

    [GeneratedRegex(
        """((?:""" + AuthHeaderNames + """)(?:\\?")?\s*:\s*(?:\\?")?\s*)([^\r\n"\\]+)""",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100
    )]
    private static partial Regex AuthHeaderRx();

    [GeneratedRegex("""\b((?:""" + SecretKeywords + """)\b[ \t]+)([^\s"\\]{16,})""", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex LabeledSecretRx();

    [GeneratedRegex(
        """([?&](?:access_token|refresh_token|id_token|client_secret|signature|sig|x-amz-signature|awsaccesskeyid|api_key|apikey|api-key|token|password|secret|auth_token|sas)=)([^&\s"\\#]+)""",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100
    )]
    private static partial Regex UrlQuerySecretRx();

    [GeneratedRegex(
        """(https?://[^:/\s"\\@]+:)([^@\s"\\/]+)(@)""",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100
    )]
    private static partial Regex UrlUserinfoRx();
}
