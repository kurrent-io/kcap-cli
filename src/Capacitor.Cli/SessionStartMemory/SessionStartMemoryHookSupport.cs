using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>
/// The two hook-side concerns every SessionStart memory adapter needs and must not get wrong,
/// factored out of the per-vendor hook commands so the next adapter inherits them instead of
/// rediscovering them. Both were real defects found in review of the Codex adapter.
///
/// <para>The vendor hooks own their envelope, eligibility and ordering — those genuinely differ per
/// harness. Only these two are identical everywhere, so only these two live here.</para>
/// </summary>
internal static class SessionStartMemoryHookSupport {
    /// <summary>
    /// Whether memory injection may attempt auth discovery for <paramref name="baseUrl"/> at all.
    ///
    /// <para>MUST be checked before any client construction. The authenticated-client helper funnels
    /// through <c>EnsureAbsolute</c>, which prints a hint and calls <c>Environment.Exit(2)</c> on a
    /// URL it cannot accept. From a hook whose host blocks on (or parses) stdout, exiting there kills
    /// the process before the required output is written, so the harness sees nothing and rejects the
    /// session — strictly worse than silently skipping an optional memory fragment.</para>
    ///
    /// <para>Deliberately the SAME predicate <c>EnsureAbsolute</c> itself uses, so this guard can
    /// never disagree with the validator it exists to protect. Single-sourced through
    /// <see cref="HookHttp.IsPostable"/>.</para>
    /// </summary>
    public static bool CanAttempt(string? baseUrl) => HookHttp.IsPostable(baseUrl);

    /// <summary>
    /// Builds the combined memory + guidelines SessionStart context provider. Both lanes share one
    /// authenticated client and the composite resolves the repo/machine scope ONCE for both. Which lanes actually run is decided per request via
    /// <see cref="SessionStartMemoryContextRequest.Disabled"/> (memory) and its
    /// <c>GuidelinesDisabled</c> flag — a disabled lane contributes nothing.
    ///
    /// <para>This is the single construction site for the eight non-Claude harnesses. Claude does NOT
    /// use it — it keeps a memory-only <see cref="SessionStartMemoryContextProvider"/> and renders
    /// guidelines from its own hook POST response.</para>
    ///
    /// <para><paramref name="client"/> stays the caller's to dispose, and both lanes send on it:
    /// <c>/api/memories/index</c> is bearer-authenticated, so a client carrying no credential leaves
    /// the harness silently without memory context on any authenticated deployment.</para>
    /// </summary>
    public static ISessionStartContextProvider CompositeProvider(
            ConfigRoot config,
            HttpClient client,
            ISessionStartMemoryScopeResolver? scopeResolver = null) {
        var resolver = scopeResolver ?? new SessionStartMemoryScopeResolver(config);

        var memory     = new SessionStartMemoryContextProvider(resolver, client);
        var guidelines = new SessionStartGuidelinesLane(client);
        return new SessionStartCompositeContextProvider(resolver, memory, guidelines);
    }

    /// <summary>
    /// Awaits an in-flight fragment fetch under the budget remaining AT THIS INSTANT — never the
    /// budget it was started with. On expiry the fetch is abandoned rather than cancelled mid-flight
    /// (its own lease bookkeeping owns that) and null is returned, so the caller's output degrades to
    /// "no memory" instead of being delayed past the harness's hook ceiling. Never throws.
    ///
    /// <para><see cref="HookBudget.Remaining"/> ALREADY reserves <see cref="HookBudget.Safety"/> for
    /// serialization and the write itself — do not subtract it again here or at the call site. Doing
    /// so cut the usable window from 3.5s to 2s at a fresh hook start and silently discarded healthy
    /// 2–3.5s responses that fit the intended ceiling.</para>
    /// </summary>
    public static async Task<string?> AwaitBounded(Task<string?> task, HookBudget budget) {
        try {
            var remaining = budget.Remaining;

            if (remaining <= TimeSpan.Zero)
                return task.IsCompletedSuccessfully ? task.Result : null;

            return await task.WaitAsync(remaining);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    /// <summary>
    /// Maps a harness-reported SessionStart source to the shared lifecycle reason. Unknown values map
    /// to <see cref="SessionLifecycleReason.Unknown"/> rather than being guessed as New — the lifecycle
    /// policy decides eligibility from this, so inventing a reason would invent an injection decision.
    /// </summary>
    public static SessionLifecycleReason ReasonFor(string? source) => source?.ToLowerInvariant() switch {
        "startup" or "new" => SessionLifecycleReason.New,
        "resume"           => SessionLifecycleReason.Resume,
        "reopen"           => SessionLifecycleReason.Reopen,
        "fork"             => SessionLifecycleReason.Fork,
        "compact"          => SessionLifecycleReason.Compact,
        null or ""         => SessionLifecycleReason.New,
        _                  => SessionLifecycleReason.Unknown
    };
}
