using System.Diagnostics;
using System.Text;

namespace Capacitor.Cli.Tests.Unit;

public class SecretRedactorTests {
    [Test]
    public async Task RedactsLine_TruncatedPemPrivateKey_DoesNotHangOnBacktracking() {
        // Regression: a tool result containing `-----BEGIN RSA PRIVATE KEY-----` followed by many
        // `\n`-escaped key body lines, then truncated WITHOUT a matching `-----END` marker, used
        // to wedge the watcher: the `(?:\\n|[\s\S])*?` alternation in PemBlockRegex had two paths
        // for every `\n` pair, producing ~2^N backtracking on the failed-to-find-END search.
        // Observed in prod (AI-783): watcher main loop at 100% CPU for 50s+ on a 5KB line.
        var keyBody = new StringBuilder();

        // Synthetic base64-like body — NOT a real key. The redactor's backtracking shape only
        // depends on the count of `\n` pairs and absence of an END marker, not on byte contents.
        for (var i = 0; i < 60; i++) {
            keyBody.Append("AAAABBBBCCCCDDDDEEEEFFFFGGGGHHHHIIIIJJJJKKKKLLLLMMMMNNNNOOOOPPPP\\n");
        }

        var line = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"toolu_1\",\"type\":\"tool_result\",\"content\":\"-----BEGIN RSA PRIVATE KEY-----\\n"
         + keyBody
         + "\",\"is_error\":false}]}}";

        var sw = Stopwatch.StartNew();
        var result = SecretRedactor.RedactLine(line);
        sw.Stop();

        // The pre-fix regex would not return inside any reasonable test budget on this input.
        // 2 seconds is generous; the fixed regex completes in <10ms.
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
        // No END marker means there's no PEM block to redact — line should pass through unchanged.
        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task RedactsLine_OverSizeLimit_ReplacesWithPlaceholder_DoesNotLeakContent() {
        // Defense in depth: lines above the size cap skip the regex pipeline entirely so a
        // future regex change reintroducing ambiguity cannot wedge the watcher. The line MUST
        // NOT be returned verbatim — WatchCommand uploads RedactLine output to the server, so
        // raw passthrough on an oversize tool-result line would be an exfiltration path.
        var secretMarker = "GHOST_TOKEN_DO_NOT_LEAK_ME_xyz12345";
        var padding      = new string('A', SecretRedactor.MaxRedactableLineChars);
        var oversized    = padding + secretMarker;

        var sw = Stopwatch.StartNew();
        var result = SecretRedactor.RedactLine(oversized);
        sw.Stop();

        await Assert.That(result).DoesNotContain(secretMarker);
        await Assert.That(result).IsEqualTo(SecretRedactor.OversizeLinePlaceholder);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(100));
    }

    [Test]
    public async Task RedactsLine_PemPrivateKey_InToolResult() {
        var line = """
            {"type":"progress","data":{"message":{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA0Z3VS5JJcds3xfn/ygWyF8PbnGy5AoG5cwSxzXf\n-----END RSA PRIVATE KEY-----","is_error":false}]}}},"uuid":"abc123","timestamp":"2026-01-01T00:00:00Z"}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("MIIEpAIBAAKCAQEA0Z3VS5JJcds3xfn");
        await Assert.That(result).Contains("[REDACTED]");
        await Assert.That(result).Contains("toolu_1"); // structure preserved
    }

    [Test]
    public async Task RedactsLine_PemPrivateKey_InToolResult_DirectFormat() {
        // Direct format: root type=user, no progress wrapper
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_2","type":"tool_result","content":"-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA0Z3VS5JJcds3xfn/ygWyF8PbnGy5AoG5cwSxzXf\n-----END RSA PRIVATE KEY-----","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("MIIEpAIBAAKCAQEA0Z3VS5JJcds3xfn");
        await Assert.That(result).Contains("[REDACTED]");
        await Assert.That(result).Contains("toolu_2"); // structure preserved
    }

    [Test]
    public async Task LeavesAssistantText_Unchanged_WhenNoSecretMatch() {
        // Plain assistant prose with no secret-matching tokens — scanned, but nothing to redact.
        var line = """{"type":"progress","data":{"message":{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hello"}]}}},"uuid":"abc","timestamp":"2026-01-01T00:00:00Z"}""";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task LeavesMalformedJson_Unchanged_WhenNoSecretMatch() {
        var line = "not json at all";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    [Arguments("ghp_ABCDEFghijklmnop1234567890abcdef12345678", "GitHub PAT")]
    [Arguments("gho_ABCDEFghijklmnop1234567890ab", "GitHub OAuth")]
    [Arguments("ghs_ABCDEFghijklmnop1234567890ab", "GitHub App")]
    [Arguments("github_pat_ABCDEF1234567890abcdef", "GitHub fine-grained PAT")]
    [Arguments("cfat_ABCDEFghijklmnop1234567890ab", "Cloudflare")]
    [Arguments("sk-proj-ABCDEFghijklmnop1234567890ab", "OpenAI")]
    [Arguments("sk_live_00000000000FAKEFAKEFAKE00", "Stripe live")]
    [Arguments("sk_test_00000000000FAKEFAKEFAKE00", "Stripe test")]
    [Arguments("xoxb-1234-5678-abcdef", "Slack bot")]
    [Arguments("xoxp-1234-5678-abcdef", "Slack user")]
    [Arguments("pypi-ABCDEFghijklmnop1234567890ab", "PyPI")]
    [Arguments("npm_ABCDEFghijklmnop1234567890ab", "npm")]
    [Arguments("glpat-ABCDEFghijklmnop12345", "GitLab PAT")]
    [Arguments("dckr_pat_ABCDEFghijklmnop1234567890", "Docker Hub PAT")]
    [Arguments("dckr_oat_J6Ui9AbcDef123XYZabcdef0987", "Docker Hub OAuth access token")]
    public async Task RedactsLine_VendorToken_InToolResult(string token, string description) {
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"The token is {{{token}}} here","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain(token);
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_AwsAccessKey_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("AKIAIOSFODNN7EXAMPLE");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    [Arguments("ASIASUOU4HYNTGJIZ23T", "STS temporary access key")]
    [Arguments("AROASUOU4HYN3THVQBHO5", "IAM role unique ID")]
    [Arguments("AIDAJQABLZS4A3QDU576Q", "IAM user unique ID")]
    [Arguments("AIPAIFHHFHABCDEF12345", "EC2 instance profile ID")]
    [Arguments("AGPAI23HXD2XYZ123ABCD", "IAM group ID")]
    public async Task RedactsLine_AwsUniqueId_InToolResult(string id, string description) {
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"id is {{{id}}} here","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        // Assert full redaction with adjacent-character locked down — a partial leak like
        // "[REDACTED]5" (21-char ID redacted as 20) would still pass DoesNotContain(id).
        await Assert.That(result).Contains("id is [REDACTED] here");
        await Assert.That(result).DoesNotContain(id);
    }

    [Test]
    public async Task RedactsLine_SecretAccessKey_InTruncatedToolOutput() {
        // Reproduces a real leak: a shell script prints only the first N chars of a JSON
        // credentials blob, so the SecretAccessKey value has no closing quote.
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"First 100 chars: {\n    \"AccessKeyId\": \"ASIASUOU4HYNTGJIZ23T\",\n    \"SecretAccessKey\": \"0X+FPiUxddhI7babryQv4l5JQ37Smuy","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("0X+FPiUxddhI7babryQv4l5JQ37Smuy");
        await Assert.That(result).DoesNotContain("ASIASUOU4HYNTGJIZ23T");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_JsonKeySecret_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"{ \"client_secret\": \"a8f3b2c91d4e7f0123456789abcdef01\" }","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("a8f3b2c91d4e7f0123456789abcdef01");
        await Assert.That(result).Contains("client_secret");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_EnvVarToken_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"HETZNER_API_TOKEN=abc123def456ghi789jkl","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("abc123def456ghi789jkl");
        await Assert.That(result).Contains("HETZNER_API_TOKEN");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_YamlStyleSecret_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"api_key: sk_reallyLongSecretValue123456","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("sk_reallyLongSecretValue123456");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_ConnectionStringPassword_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"Server=localhost;Database=mydb;Password=hunter2;User Id=sa","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("hunter2");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task DoesNotRedact_CleanToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"total 40\ndrwxr-xr-x  8 user staff 256 Feb 11 22:54 .\n-rw-r--r--  1 user staff 611 Feb 10 22:33 Program.cs","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task RedactsLine_SecretInToolUseResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"ok","is_error":false}]},"toolUseResult":{"content":"-----BEGIN EC PRIVATE KEY-----\nMHQCAQEEIBkg\n-----END EC PRIVATE KEY-----"}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("MHQCAQEEIBkg");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task LeavesUserText_Unchanged_WhenSecretValueTooShort() {
        // "hunter2" is below LabeledSecretRegex's 16-char floor; no pattern matches.
        // (A real high-entropy secret in the same shape WOULD be redacted — see
        // RedactsLine_VendorToken_InUserTextContent.)
        var line = """{"type":"user","message":{"role":"user","content":"my secret password is hunter2"}}""";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task LeavesToolUse_Unchanged_WhenNoSecretMatch() {
        // tool_use args reference a file path; "/etc/secret.pem" has no key:value or labeled-secret
        // shape, so the line passes through scanning unmodified.
        var line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"/etc/secret.pem"}}]}}""";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task RedactsLine_MultipleSecrets_InSameToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"GITHUB_TOKEN=ghp_abc123def456ghi789jkl012mno345pqrs678\nAWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE\nDB_PASSWORD=supersecretvalue123","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_abc123");
        await Assert.That(result).DoesNotContain("AKIAIOSFODNN7EXAMPLE");
        await Assert.That(result).DoesNotContain("supersecretvalue123");
    }

    // Scope-widening coverage — secrets must be redacted regardless of whether they appear in a
    // tool_result, assistant narrative, or human-authored user turn. The original implementation
    // only scanned user messages containing a tool_result block, which let the assistant trivially
    // re-emit a token by quoting it back when explaining what a command did.

    [Test]
    public async Task RedactsLine_DockerToken_InAssistantText() {
        // Real leak shape (AI-): agent paraphrased kubectl/docker output into its own narrative,
        // re-emitting a dckr_oat_ token that had been redacted from the originating tool_result.
        var line = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Image pushed but the pull token (dckr_oat_J6Ui9AbcDef123XYZabcdef0987) used by the cluster's registry-pull Secret is scoped to kurrentplatform/kcap only."}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("dckr_oat_J6Ui9AbcDef123XYZabcdef0987");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_VendorToken_InAssistantText_ProgressFormat() {
        var line = """
            {"type":"progress","data":{"message":{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I used ghp_ABCDEFghijklmnop1234567890abcdef12345678 to authenticate."}]}}},"uuid":"abc","timestamp":"2026-01-01T00:00:00Z"}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_ABCDEFghijklmnop1234567890abcdef12345678");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_VendorToken_InUserTextContent() {
        // Free-form user turn (string content, not the tool_result array form) — a human paste of
        // a token must also be redacted before the line is uploaded.
        var line = """
            {"type":"user","message":{"role":"user","content":"my token is ghp_ABCDEFghijklmnop1234567890abcdef12345678 please rotate"}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_ABCDEFghijklmnop1234567890abcdef12345678");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task PassesThrough_EmptyLine_Unchanged() {
        await Assert.That(SecretRedactor.RedactLine("")).IsEqualTo("");
    }

    [Test]
    public async Task PassesThrough_WhitespaceLine_Unchanged() {
        await Assert.That(SecretRedactor.RedactLine("   ")).IsEqualTo("   ");
    }

    [Test]
    public async Task PreservesValidJson_AfterEnvVarRedaction() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"HETZNER_API_TOKEN=abc123def456ghi789jkl","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        // Must still be valid JSON after redaction
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var content = doc.RootElement
            .GetProperty("message")
            .GetProperty("content");
        // is_error field must survive redaction
        await Assert.That(content[0].GetProperty("is_error").GetBoolean()).IsFalse();
    }

    // AI-50 — auth headers recorded as-is

    [Test]
    public async Task RedactsLine_AuthorizationBearer_InCurlToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"$ curl -fsS -H \"Authorization: Bearer abc123def456ghi789jkl012mno\" https://api.example.com\n{\"ok\":true}","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("abc123def456ghi789jkl012mno");
        await Assert.That(result).Contains("Authorization: [REDACTED]");
    }

    [Test]
    public async Task RedactsLine_AuthorizationBasic_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"Authorization: Basic dXNlcjpwYXNzd29yZA==","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("dXNlcjpwYXNzd29yZA==");
        await Assert.That(result).Contains("Authorization: [REDACTED]");
    }

    [Test]
    public async Task RedactsLine_CookieHeader_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"> Cookie: session=abc123def456; csrf=xyz789uvw321","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("session=abc123def456");
        await Assert.That(result).DoesNotContain("csrf=xyz789uvw321");
        await Assert.That(result).Contains("Cookie: [REDACTED]");
    }

    [Test]
    public async Task RedactsLine_XApiKeyHeader_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"X-Api-Key: opaque_value_with_no_known_prefix_12345","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("opaque_value_with_no_known_prefix_12345");
        await Assert.That(result).Contains("X-Api-Key: [REDACTED]");
    }

    [Test]
    public async Task RedactsLine_AuthorizationHeader_AsJsonObjectField() {
        // HTTP request logged as a JSON object (escaped quotes around key and value).
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"request headers: { \"Authorization\": \"Bearer abc123def456ghi789jkl012mno\", \"Accept\": \"application/json\" }","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("abc123def456ghi789jkl012mno");
        await Assert.That(result).Contains("[REDACTED]");

        // Result must remain valid JSON after redaction.
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("is_error").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task RedactsLine_AuthorizationHeader_AsRealJsonObject() {
        // Real JSON object (NOT a string-encoded one) with Authorization as a true key.
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"ok"}]},"toolUseResult":{"headers":{"Authorization":"Bearer opaque-token-1234567890abc"}}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("opaque-token-1234567890abc");
        await Assert.That(result).Contains("[REDACTED]");

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetProperty("toolUseResult").GetProperty("headers").GetProperty("Authorization").GetString()).IsEqualTo("[REDACTED]");
    }

    [Test]
    [Arguments("Private-Token", "glpat-AAAAAAAAAAAAAAAAAAAA")]
    [Arguments("Job-Token", "ci_job_token_value_1234567890")]
    [Arguments("Deploy-Token", "deploy_token_value_1234567890")]
    [Arguments("X-Vault-Token", "hvs.AAAAAAAAAAAAAAAAAAAAAAAA")]
    [Arguments("X-Consul-Token", "00000000-0000-0000-0000-000000000000")]
    [Arguments("X-CSRF-Token", "csrf_value_abcdef1234567890")]
    [Arguments("X-XSRF-TOKEN", "xsrf_value_abcdef1234567890")]
    [Arguments("X-Hub-Signature-256", "sha256=abcdef1234567890abcdef1234567890")]
    [Arguments("X-Slack-Signature", "v0=abcdef1234567890abcdef1234567890")]
    [Arguments("Stripe-Signature", "t=1492774577,v1=5257a86")]
    [Arguments("X-Registry-Auth", "ewogICJhdXRoIjogImFiY2QifQo=")]
    public async Task RedactsLine_AdditionalAuthHeaders_InToolResult(string header, string value) {
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"{{{header}}}: {{{value}}}","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain(value);
        await Assert.That(result).Contains($"{header}: [REDACTED]");
    }

    // Finding 4 coverage — URL query secrets and userinfo

    [Test]
    [Arguments("https://example.com/path?access_token=at_abcdef1234567890&user=alice")]
    [Arguments("https://example.com/cb?id_token=jwt.eyJhbGciOi.payload&state=xyz")]
    [Arguments("https://api.example.com/x?signature=abcdef1234567890abcdef&v=1")]
    [Arguments("https://s3.amazonaws.com/b/k?AWSAccessKeyId=AKIAIOSFODNN7EXAMPLE&Signature=abc%2F123&Expires=1")]
    [Arguments("https://example.com/h?api_key=key_abcdef1234567890")]
    public async Task RedactsLine_UrlQuerySecret_InToolResult(string url) {
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"GET {{{url}}} -> 200","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).Contains("[REDACTED]");
        // The secret param value must not survive — check by ensuring no `[?&]<param>=<longvalue>` is left.
        await Assert.That(result).DoesNotContain("at_abcdef1234567890");
        await Assert.That(result).DoesNotContain("jwt.eyJhbGciOi.payload");
        await Assert.That(result).DoesNotContain("abcdef1234567890abcdef");
        await Assert.That(result).DoesNotContain("AKIAIOSFODNN7EXAMPLE");
        await Assert.That(result).DoesNotContain("key_abcdef1234567890");
    }

    [Test]
    public async Task RedactsLine_UrlUserinfo_InToolResult() {
        // RFC 3986 userinfo: literal `@` must be percent-encoded, so the password component cannot
        // legally contain `@`. Realistic test case is a Git URL with an opaque-token password.
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"cloning https://alice:ghp_abcdef1234567890ABCDEF@github.com/org/repo.git","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_abcdef1234567890ABCDEF");
        await Assert.That(result).Contains("https://alice:[REDACTED]@github.com");
    }

    // AI-53 — labeled secrets with no colon separator (e.g. `hcloud:token  <value>`)

    [Test]
    public async Task RedactsLine_LabeledSecret_HcloudToken_InToolResult() {
        // `hcloud:token` is a label, not a key:value separator. Token value below is a synthetic
        // 64-char placeholder matching Hetzner's format — never paste a real token here.
        const string fakeHcloudToken = "FAKE_HCLOUD_TOKEN_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"hcloud:token  {{{fakeHcloudToken}}}","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain(fakeHcloudToken);
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_LabeledSecret_PasswordWithSpace() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"password    supersecretvalue1234567890","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("supersecretvalue1234567890");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_LabeledSecret_PasswordWithPunctuation() {
        // Value with `!`, `@`, `#`, etc. — broader charset must catch the full opaque value, not
        // stop at the first non-alnum char.
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"password abcdefghijklmnop!tail#more$end","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("abcdefghijklmnop!tail#more$end");
        await Assert.That(result).DoesNotContain("!tail");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task DoesNotRedact_LabeledSecret_ProseAboutSecrets() {
        // Negative — prose about secrets must survive. Short word "might" after "token" stops the
        // 16+ run, and there are no header keywords either.
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"the token might be invalid; refresh it","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task DoesNotRedact_LabeledSecret_ShortValue() {
        // Negative — values below the 16-char floor are noise (version numbers, IDs, etc.).
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"token short1234","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task PreservesValidJson_AfterConnectionStringRedaction() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"Server=localhost;Database=mydb;Password=hunter2;User Id=sa","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var content = doc.RootElement
            .GetProperty("message")
            .GetProperty("content");
        await Assert.That(content[0].GetProperty("is_error").GetBoolean()).IsFalse();
    }
}
