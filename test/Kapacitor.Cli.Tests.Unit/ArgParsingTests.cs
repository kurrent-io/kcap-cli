namespace Kapacitor.Cli.Tests.Unit;

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
        // `kapacitor eval sess-abc --model sonnet` — positional before flags.
        var id = ArgParsing.ResolveSessionId(
            ["eval", "sess-abc", "--model", "sonnet"],
            valueFlags: ["--model"]
        );

        await Assert.That(id).IsEqualTo("sess-abc");
    }

    // These tests mutate KAPACITOR_SESSION_ID / CODEX_THREAD_ID and must not run
    // in parallel with each other (TUnit parallelizes by default).
    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionId_returns_env_fallback_when_only_flags_present() {
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);

        var id = ArgParsing.ResolveSessionId(
            ["eval", "--model", "sonnet", "--chain"],
            valueFlags: ["--model"]
        );

        await Assert.That(id).IsNull();
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionId_honors_env_when_no_positional() {
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, "envsess");

        try {
            var id = ArgParsing.ResolveSessionId(
                ["eval", "--model", "sonnet"],
                valueFlags: ["--model"]
            );

            await Assert.That(id).IsEqualTo("envsess");
        } finally {
            Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
        }
    }

    const string KapacitorSessionIdEnvVar = "KAPACITOR_SESSION_ID";
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
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      null);

        var id = ArgParsing.ResolveSessionIdFromEnv();

        await Assert.That(id).IsNull();
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionIdFromEnv_returns_kapacitor_env_stripped_of_dashes() {
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, "abc-def-123");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      null);

        try {
            var id = ArgParsing.ResolveSessionIdFromEnv();

            await Assert.That(id).IsEqualTo("abcdef123");
        } finally {
            Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
        }
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionIdFromEnv_returns_codex_env_stripped_of_dashes_when_kapacitor_unset() {
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      "thread-uuid-1");

        try {
            var id = ArgParsing.ResolveSessionIdFromEnv();

            await Assert.That(id).IsEqualTo("threaduuid1");
        } finally {
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);
        }
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionIdFromEnv_prefers_kapacitor_over_codex() {
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, "kap-1");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      "cdx-2");

        try {
            var id = ArgParsing.ResolveSessionIdFromEnv();

            await Assert.That(id).IsEqualTo("kap1");
        } finally {
            Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      null);
        }
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionIdFromEnv_treats_whitespace_kapacitor_as_unset_and_falls_back_to_codex() {
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, "   ");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      "cdx-3");

        try {
            var id = ArgParsing.ResolveSessionIdFromEnv();

            await Assert.That(id).IsEqualTo("cdx3");
        } finally {
            Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      null);
        }
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionId_falls_back_to_codex_env_when_no_args_and_no_kapacitor_env() {
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      "cdx-only");

        try {
            var id = ArgParsing.ResolveSessionId(
                ["eval", "--model", "sonnet"],
                valueFlags: ["--model"]
            );

            await Assert.That(id).IsEqualTo("cdxonly");
        } finally {
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);
        }
    }

    [Test]
    [NotInParallel(nameof(SessionEnvVarMutation))]
    public async Task ResolveSessionId_strips_dashes_from_kapacitor_env_fallback() {
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, "abc-def");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar,      null);

        try {
            var id = ArgParsing.ResolveSessionId(
                ["eval", "--model", "sonnet"],
                valueFlags: ["--model"]
            );

            await Assert.That(id).IsEqualTo("abcdef");
        } finally {
            Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
        }
    }
}
