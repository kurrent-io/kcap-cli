namespace Capacitor.Cli.Tests.Unit;

public class ArgParsingTests {
    [Test]
    public async Task ResolveSessionId_returns_first_positional_when_no_flags() {
        var id = ArgParsing.ResolveSessionId(["eval", "sess-123"]);

        await Assert.That(id).IsEqualTo("sess-123");
    }

    [Test]
    public async Task ResolveSessionId_skips_boolean_flags_and_picks_positional() {
        var id = ArgParsing.ResolveSessionId(["recap", "--chain", "--full", "sess-abc"]);

        await Assert.That(id).IsEqualTo("sess-abc");
    }

    [Test]
    public async Task ResolveSessionId_skips_value_of_known_value_bearing_flag() {
        // The bug: without valueFlags, "sonnet" (the value of --model) was
        // returned as the sessionId.
        var id = ArgParsing.ResolveSessionId(
            ["eval", "--model", "sonnet", "sess-xyz"],
            valueFlags: ["--model"]
        );

        await Assert.That(id).IsEqualTo("sess-xyz");
    }

    [Test]
    public async Task ResolveSessionId_skips_multiple_value_flags() {
        var id = ArgParsing.ResolveSessionId(
            ["eval", "--model", "opus", "--threshold", "5000", "sess-zzz"],
            valueFlags: ["--model", "--threshold"]
        );

        await Assert.That(id).IsEqualTo("sess-zzz");
    }

    [Test]
    public async Task ResolveSessionId_handles_flags_after_positional() {
        // `kcap eval sess-abc --model sonnet` — positional before flags.
        var id = ArgParsing.ResolveSessionId(
            ["eval", "sess-abc", "--model", "sonnet"],
            valueFlags: ["--model"]
        );

        await Assert.That(id).IsEqualTo("sess-abc");
    }

    // These tests mutate KCAP_SESSION_ID / CODEX_THREAD_ID and must not run
    // in parallel with each other (TUnit parallelizes by default).
    //
    // Every SessionEnvVarMutation test saves AND restores BOTH env vars,
    // even when the test body only manipulates one of them. The test runner
    // can be invoked from inside an active Codex session — where
    // CODEX_THREAD_ID is set process-wide — and any test that reads the env
    // would otherwise observe that leaked value as a fallback session id and
    // fail nondeterministically depending on where it was run.
    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionId_returns_env_fallback_when_only_flags_present() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, null);
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      null);

        try {
            var id = ArgParsing.ResolveSessionId(
                ["eval", "--model", "sonnet", "--chain"],
                valueFlags: ["--model"]
            );

            await Assert.That(id).IsNull();
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      savedCdx);
        }
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionId_honors_env_when_no_positional() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, "envsess");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      null);

        try {
            var id = ArgParsing.ResolveSessionId(
                ["eval", "--model", "sonnet"],
                valueFlags: ["--model"]
            );

            await Assert.That(id).IsEqualTo("envsess");
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      savedCdx);
        }
    }

    const string CapacitorSessionIdEnvVar = "KCAP_SESSION_ID";
    const string CodexThreadIdEnvVar      = "CODEX_THREAD_ID";
    const string SessionEnvVarMutation    = nameof(SessionEnvVarMutation);

    [Test]
    public async Task ResolveSessionId_does_not_eat_next_arg_when_flag_is_not_value_bearing() {
        // --chain is boolean; sess-abc must still be returned even if it
        // sits right after --chain.
        var id = ArgParsing.ResolveSessionId(
            ["eval", "--chain", "sess-abc"],
            valueFlags: ["--model"]
        );

        await Assert.That(id).IsEqualTo("sess-abc");
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionIdFromEnv_returns_null_when_neither_env_set() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, null);
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      null);

        try {
            var id = ArgParsing.ResolveSessionIdFromEnv();

            await Assert.That(id).IsNull();
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      savedCdx);
        }
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionIdFromEnv_returns_kcap_env_stripped_of_dashes() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, "abc-def-123");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      null);

        try {
            var id = ArgParsing.ResolveSessionIdFromEnv();

            await Assert.That(id).IsEqualTo("abcdef123");
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      savedCdx);
        }
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionIdFromEnv_returns_codex_env_stripped_of_dashes_when_kcap_unset() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, null);
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      "thread-uuid-1");

        try {
            var id = ArgParsing.ResolveSessionIdFromEnv();

            await Assert.That(id).IsEqualTo("threaduuid1");
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      savedCdx);
        }
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionIdFromEnv_prefers_kcap_over_codex() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, "kap-1");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      "cdx-2");

        try {
            var id = ArgParsing.ResolveSessionIdFromEnv();

            await Assert.That(id).IsEqualTo("kap1");
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      savedCdx);
        }
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionIdFromEnv_treats_whitespace_kcap_as_unset_and_falls_back_to_codex() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, "   ");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      "cdx-3");

        try {
            var id = ArgParsing.ResolveSessionIdFromEnv();

            await Assert.That(id).IsEqualTo("cdx3");
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      savedCdx);
        }
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionId_falls_back_to_codex_env_when_no_args_and_no_kcap_env() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, null);
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      "cdx-only");

        try {
            var id = ArgParsing.ResolveSessionId(
                ["eval", "--model", "sonnet"],
                valueFlags: ["--model"]
            );

            await Assert.That(id).IsEqualTo("cdxonly");
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      savedCdx);
        }
    }

    [Test]
    public async Task TryNormalizeSessionGuid_accepts_dashed_uuid_and_returns_dashless() {
        var ok = ArgParsing.TryNormalizeSessionGuid("01234567-89ab-cdef-0123-456789abcdef", out var canonical);
        await Assert.That(ok).IsTrue();
        await Assert.That(canonical).IsEqualTo("0123456789abcdef0123456789abcdef");
    }

    [Test]
    public async Task TryNormalizeSessionGuid_accepts_dashless_uuid_unchanged() {
        var ok = ArgParsing.TryNormalizeSessionGuid("0123456789abcdef0123456789abcdef", out var canonical);
        await Assert.That(ok).IsTrue();
        await Assert.That(canonical).IsEqualTo("0123456789abcdef0123456789abcdef");
    }

    [Test]
    public async Task TryNormalizeSessionGuid_rejects_slug() {
        var ok = ArgParsing.TryNormalizeSessionGuid("foo-bar-baz", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryNormalizeSessionGuid_rejects_path_traversal() {
        var ok = ArgParsing.TryNormalizeSessionGuid("../etc/passwd", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryNormalizeSessionGuid_rejects_empty() {
        var ok = ArgParsing.TryNormalizeSessionGuid("", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionId_strips_dashes_from_kcap_env_fallback() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, "abc-def");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      null);

        try {
            var id = ArgParsing.ResolveSessionId(
                ["eval", "--model", "sonnet"],
                valueFlags: ["--model"]
            );

            await Assert.That(id).IsEqualTo("abcdef");
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      savedCdx);
        }
    }
}
