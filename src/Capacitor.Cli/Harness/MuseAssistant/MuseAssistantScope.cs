using System.Globalization;
using System.Security.Cryptography;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>
/// Grants, revocations and their replay. Every command that rewrites the chat list takes the scope lock,
/// then the chat lock, so a grant, a revocation and a run are totally ordered per chat and two adds for
/// different chats cannot drop each other's entry. State is never deleted; an unusable file is renamed aside.
/// </summary>
public sealed class MuseAssistantScope(
        ConfigRoot                     config,
        MuseAssistantIdentity          identity,
        MuseAssistantStateStoreFactory stores,
        MuseAssistantActionRunner      runner,
        TimeProvider                   time
    ) {
    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(60);
    // Every scope command and sweep holds the scope lock before any chat lock, so a chat lock another
    // process holds there is an ingest's; the scope lock itself is only ever another scope command's.
    const string Busy      = "ingest in progress, retry";
    const string ScopeBusy = "another scope command or sweep is running, retry";

    /// <summary>The add as the command line states it: a boundary that does not parse is refused before
    /// anything is resolved or written.</summary>
    public async Task<int> AddAsync(string chatId, string[] args, CancellationToken ct) {
        var (boundary, error) = MuseAssistantBoundary.Parse(args);
        if (boundary is null) return Fail("scope add", MuseAssistantExitCodes.Malformed, error!);

        return await AddAsync(chatId, boundary, ct);
    }

    public Task<int> AddAsync(string chatId, MuseAssistantBoundary boundary, CancellationToken ct) =>
        UnderLocksAsync("scope add", chatId, MuseAssistantExitCodes.Server, async (principal, store) => {
            var (state, stateProblem) = store.LoadState(chatId);
            if (stateProblem is not null) return Fail("scope add", MuseAssistantExitCodes.OutOfScope, stateProblem);

            var now = time.GetUtcNow().ToUnixTimeMilliseconds();

            if (state is null) {
                state = new(chatId, principal.ServerUrl, principal.UserId, NewGrant(boundary, now), null, null, null);
                store.SaveState(state);
            } else if (state.Grant.Revoked) {
                if (boundary.Kind == MuseAssistantGrantBoundaries.EmptyChat && state.Watermark is not null)
                    return Fail("scope add", MuseAssistantExitCodes.Malformed, "--empty-chat cannot re-grant a chat that has already been recorded: items written while recording was off would be sent; use --after-seq <seq> or --include-history");

                if (await runner.ExecuteAsync(store, MuseAssistantPlanner.Closure(state), chatId, ct) is { } refused)
                    return Fail("scope add", MuseAssistantExitCodes.Server, refused);

                var (reloaded, reloadProblem) = store.LoadState(chatId);
                if (reloaded is null) return Fail("scope add", MuseAssistantExitCodes.OutOfScope, reloadProblem ?? "the state file for this chat disappeared");

                state = reloaded with { Grant = NewGrant(boundary, now) };
                store.SaveState(state);
            } else if (!Matches(boundary, state.Grant)) {
                // Exit 0 would confirm a consent the grant does not carry: a wider boundary promises items it
                // never selects, a narrower one would keep sending items the user just declined.
                return Fail("scope add", MuseAssistantExitCodes.Malformed, $"chat already recorded with {DescribeBoundary(state.Grant)}; run `kcap muse-assistant scope remove --chat \"{chatId}\"` then `scope add` with the new boundary");
            } else if (store.LoadScope().Chats.Contains(chatId, StringComparer.Ordinal)) {
                Console.Out.WriteLine($"already granted {Describe(state)}");
                return MuseAssistantExitCodes.Ok;
            }

            List(store, chatId);
            Console.Out.WriteLine($"granted {Describe(state)}");
            return MuseAssistantExitCodes.Ok;
        }, ct);

    public Task<int> RemoveAsync(string chatId, CancellationToken ct) =>
        UnderLocksAsync("scope remove", chatId, MuseAssistantExitCodes.NotRevoked, async (_, store) => {
            var (state, stateProblem) = store.LoadState(chatId);
            if (stateProblem is not null) {
                var aside = store.SetAsideState(chatId, time.GetUtcNow().ToUnixTimeMilliseconds());
                Unlist(store, chatId);
                return Fail("scope remove", MuseAssistantExitCodes.OutOfScope, $"{stateProblem}; set aside as {aside}, and the chat is no longer listed, so nothing more is recorded; `scope add` now grants it afresh");
            }

            if (state is null) {
                if (!store.LoadScope().Chats.Contains(chatId, StringComparer.Ordinal)) {
                    Console.Out.WriteLine($"no grant recorded chat={chatId}");
                    return MuseAssistantExitCodes.Ok;
                }

                Unlist(store, chatId);
                return Fail("scope remove", MuseAssistantExitCodes.OutOfScope, $"no grant recorded chat={chatId}; its state file is missing, so the chat is no longer listed and nothing more is recorded, but a session it had open may stay open; `scope add` grants it afresh");
            }

            if (!state.Grant.Revoked) state = Revoke(store, state);

            if (await runner.ExecuteAsync(store, MuseAssistantPlanner.Closure(state), chatId, ct) is { } refused)
                return Fail("scope remove", MuseAssistantExitCodes.Server, $"{refused}; recording stopped here, and `kcap muse-assistant sweep` tells the server chat={chatId}");

            Unlist(store, chatId);
            Console.Out.WriteLine($"removed chat={chatId}");
            return MuseAssistantExitCodes.Ok;
        }, ct, whenChatBusy: store => RevokedWhileBusy(store, chatId));

    /// <summary>A removal that cannot get the chat lock may still find its work already done: the state
    /// file is replaced atomically, so reading it unlocked is safe. Null when the grant is not revoked.</summary>
    static int? RevokedWhileBusy(MuseAssistantStateStore store, string chatId) {
        if (store.LoadState(chatId).State is not { Grant.Revoked: true } state) return null;

        if (state.Open is not null) {
            Console.Error.WriteLine($"kcap muse-assistant scope remove: {Busy} chat={chatId}; revoked; the end is still being delivered, and `kcap muse-assistant sweep` finishes it");
            return MuseAssistantExitCodes.Server;
        }

        Unlist(store, chatId);
        Console.Error.WriteLine($"kcap muse-assistant scope remove: {Busy} chat={chatId}; already revoked and nothing is left to deliver");
        return MuseAssistantExitCodes.Ok;
    }

    /// <summary>Revokes every chat it can lock, sets aside every state file it cannot use and unlists a listed
    /// chat with no state file, exiting 3 for either of the last two. One it cannot lock stays granted and
    /// listed — dropping it from the list alone would be undone by the next sweep — and the exit says
    /// recording has not stopped.</summary>
    public Task<int> ResetAsync(CancellationToken ct) =>
        UnderLocksAsync("scope reset", null, MuseAssistantExitCodes.NotRevoked, async (_, store) => {
            var unrevoked = new HashSet<string>(StringComparer.Ordinal);
            var failed    = false;
            var setAside  = false;

            // The caller holds the chat lock.
            async Task RevokeLockedAsync(MuseAssistantChatState found) {
                var state = store.LoadState(found.ChatId).State ?? found;
                if (!state.Grant.Revoked) state = Revoke(store, state);

                if (await runner.ExecuteAsync(store, MuseAssistantPlanner.Closure(state), state.ChatId, ct) is { } refused) {
                    failed = true;
                    Console.Error.WriteLine($"kcap muse-assistant scope reset: {refused}; recording stopped here, and `kcap muse-assistant sweep` tells the server about \"{state.ChatId}\"");
                }
            }

            // An ingest holds only its chat lock, so no file check before it is held says what the chat's state is.
            var listed = store.LoadScope().Chats.ToHashSet(StringComparer.Ordinal);
            var seen   = store.EnumerateStates().ToDictionary(x => x.ChatId, StringComparer.Ordinal);

            foreach (var chatId in listed.Union(seen.Keys).ToList()) {
                using var chatLock = await LockAsync(MuseAssistantPaths.ChatLock(store.ScopeDir, chatId), ct);
                if (chatLock is null) {
                    if (seen.TryGetValue(chatId, out var found) && found.Grant.Revoked) {
                        // Already revoked, so recording is stopped; `chat=` is kept for the chats that may still record.
                        Console.Error.WriteLine($"kcap muse-assistant scope reset: {Busy}; already revoked, recording is stopped for \"{chatId}\"");
                        if (found.Open is not null) failed = true;
                    } else {
                        Console.Error.WriteLine($"kcap muse-assistant scope reset: {Busy} chat={chatId}");
                        unrevoked.Add(chatId);
                    }

                    continue;
                }

                var (state, problem) = store.LoadState(chatId);
                if (state is not null) {
                    await RevokeLockedAsync(state);
                    continue;
                }

                // An unusable file is set aside by the sweep over unusable files below.
                if (problem is not null || !listed.Contains(chatId)) continue;

                setAside = true;
                Console.Error.WriteLine($"kcap muse-assistant scope reset: no grant recorded for \"{chatId}\"; its state file is missing, so nothing more is recorded from it, but a session it had open may stay open");
            }

            var listedByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var chatId in store.LoadScope().Chats) listedByKey.TryAdd(MuseAssistantPaths.ChatKey(chatId), chatId);

            foreach (var file in store.EnumerateUnusableStateFiles().ToList()) {
                var listedAs = listedByKey.GetValueOrDefault(Path.GetFileNameWithoutExtension(file));

                using var fileLock = await LockAsync(Path.ChangeExtension(file, ".lock"), ct);
                if (fileLock is null) {
                    if (listedAs is null) {
                        Console.Error.WriteLine($"kcap muse-assistant scope reset: {Busy}; the unusable state file {Path.GetFileName(file)} was not set aside");
                    } else {
                        Console.Error.WriteLine($"kcap muse-assistant scope reset: {Busy}; its state file is unusable and was not set aside chat={listedAs}");
                        unrevoked.Add(listedAs);
                    }

                    continue;
                }

                var aside = store.SetAside(file, time.GetUtcNow().ToUnixTimeMilliseconds());
                setAside = true;
                var subject = listedAs is null ? Path.GetFileName(file) : $"the state file of \"{listedAs}\"";
                Console.Error.WriteLine($"kcap muse-assistant scope reset: {subject} is unreadable or belongs to another account; set aside as {aside}, so nothing more is recorded from it, but a session it had open may stay open");
            }

            var scope = store.LoadScope();
            store.SaveScope(scope with { Chats = scope.Chats.Where(unrevoked.Contains).ToArray() });

            if (unrevoked.Count > 0) return MuseAssistantExitCodes.NotRevoked;

            Console.Out.WriteLine(setAside ? "reset: every grant revoked or set aside" : "reset: every grant revoked");
            return failed ? MuseAssistantExitCodes.Server : setAside ? MuseAssistantExitCodes.OutOfScope : MuseAssistantExitCodes.Ok;
        }, ct);

    public async Task<int> ShowAsync(CancellationToken ct) {
        var (principal, problem) = await identity.ResolveAsync(ct);
        if (principal is null) return Fail("scope show", MuseAssistantExitCodes.Server, problem!);

        var store  = stores.Create(MuseAssistantPaths.ScopeDir(config, principal), principal);
        var listed = store.LoadScope().Chats.ToHashSet(StringComparer.Ordinal);
        var states = store.EnumerateStates().ToDictionary(s => s.ChatId, StringComparer.Ordinal);

        foreach (var chatId in listed.Union(states.Keys).Order(StringComparer.Ordinal)) {
            if (!states.TryGetValue(chatId, out var state)) {
                Console.Out.WriteLine($"status=no_state key={MuseAssistantPaths.ChatKey(chatId)} chat={chatId}");
                continue;
            }

            var status = state.Grant.Revoked ? (state.Open is null ? "revoked" : "closing") : "active";
            Console.Out.WriteLine($"status={status} open={state.Open?.Phase ?? "none"} {Describe(state)}");
        }

        return MuseAssistantExitCodes.Ok;
    }

    /// <summary>Replays recorded intents and reconciles the two files; prints every active chat for the
    /// cron. Never idle-closes: that needs the chat's current head, which only the ingest has seen. A listed
    /// chat with no usable state is not printed, so the cron would otherwise read the run as clean:
    /// it exits 3 naming each such chat on stderr, or 1 when a lifecycle post also failed.</summary>
    public Task<int> SweepAsync(CancellationToken ct) =>
        UnderLocksAsync("sweep", null, MuseAssistantExitCodes.Server, async (_, store) => {
            var listed = store.LoadScope().Chats.ToHashSet(StringComparer.Ordinal);
            var states = store.EnumerateStates().ToDictionary(s => s.ChatId, StringComparer.Ordinal);
            var failed = false;
            var stateless = false;
            var active = new List<string>();

            foreach (var chatId in listed.Union(states.Keys).Order(StringComparer.Ordinal)) {
                if (!states.TryGetValue(chatId, out var state)) {
                    stateless = true;
                    var guidance = File.Exists(MuseAssistantPaths.ChatState(store.ScopeDir, chatId))
                        ? "the state file is unreadable or belongs to another account; `scope remove` sets it aside, then `scope add` records the chat afresh"
                        : "run `scope remove` then `scope add`";
                    Console.Error.WriteLine($"kcap muse-assistant sweep: no usable state; {guidance} chat={chatId}");
                    continue;
                }

                // A wait here would hold the scope lock against a user-turn scope command. An ingest holding
                // the chat replays its own intents; a revoked closure is left for the next sweep.
                using var chatLock = MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(store.ScopeDir, chatId));
                if (chatLock is null) {
                    Console.Error.WriteLine($"kcap muse-assistant sweep: {Busy} chat={chatId}");
                    if (!state.Grant.Revoked) active.Add(Describe(state));
                    continue;
                }

                state = store.LoadState(chatId).State ?? state;
                var actions = state.Grant.Revoked ? MuseAssistantPlanner.Closure(state) : MuseAssistantPlanner.Replay(state);

                if (await runner.ExecuteAsync(store, actions, chatId, ct) is { } refused) {
                    failed = true;
                    Console.Error.WriteLine($"kcap muse-assistant sweep: {refused} chat={chatId}");
                    if (!state.Grant.Revoked) active.Add(Describe(state));
                    continue;
                }

                state = store.LoadState(chatId).State ?? state;

                if (state.Grant.Revoked) {
                    if (state.Open is null && listed.Contains(chatId)) Unlist(store, chatId);
                    continue;
                }

                if (!listed.Contains(chatId)) List(store, chatId);
                active.Add(Describe(state));
            }

            foreach (var line in active) Console.Out.WriteLine(line);

            return failed ? MuseAssistantExitCodes.Server : stateless ? MuseAssistantExitCodes.OutOfScope : MuseAssistantExitCodes.Ok;
        }, ct);

    /// <summary>Resolves the profile's store and runs <paramref name="body"/> holding the scope lock, then
    /// the chat lock when <paramref name="chatId"/> names one. Failing to get either — or the identity — is
    /// reported with <paramref name="unavailable"/>, before anything is written.</summary>
    async Task<int> UnderLocksAsync(
            string verb, string? chatId, int unavailable, Func<MuseAssistantPrincipal, MuseAssistantStateStore, Task<int>> body, CancellationToken ct,
            Func<MuseAssistantStateStore, int?>? whenChatBusy = null) {
        var (principal, problem) = await identity.ResolveAsync(ct);
        if (principal is null) return Fail(verb, unavailable, problem!);

        var store = stores.Create(MuseAssistantPaths.ScopeDir(config, principal), principal);

        using var scopeLock = await LockAsync(MuseAssistantPaths.ScopeLock(store.ScopeDir), ct);
        if (scopeLock is null) return Fail(verb, unavailable, ScopeBusy);
        if (chatId is null) return await body(principal, store);

        using var chatLock = await LockAsync(MuseAssistantPaths.ChatLock(store.ScopeDir, chatId), ct);
        if (chatLock is null) return whenChatBusy?.Invoke(store) ?? Fail(verb, unavailable, $"{Busy} chat={chatId}");

        return await body(principal, store);
    }

    Task<FileStream?> LockAsync(string path, CancellationToken ct) => MuseAssistantChatLock.AcquireAsync(path, LockWait, time, ct);

    static bool Matches(MuseAssistantBoundary requested, MuseAssistantGrant grant) =>
        requested.Kind == grant.Boundary && (requested.Kind != MuseAssistantGrantBoundaries.AfterSeq || requested.AfterSeq == grant.AfterSeq);

    static MuseAssistantGrant NewGrant(MuseAssistantBoundary boundary, long now) =>
        new(RandomNumberGenerator.GetHexString(32, lowercase: true), boundary.Kind, boundary.AfterSeq, now, null);

    // The revocation and the closure intent land in one write, so a run that passed validation before
    // this cannot post past it.
    MuseAssistantChatState Revoke(MuseAssistantStateStore store, MuseAssistantChatState state) {
        var open = state.Open switch {
            null                                                       => null,
            { Phase: MuseAssistantSessionPhases.PendingStart } pending => pending,
            var o => o with { Phase = MuseAssistantSessionPhases.PendingEnd, EndedAtMs = MuseAssistantPlanner.SessionEndedAtMs(state, o) }
        };
        var revoked = state with { Grant = state.Grant with { RevokedAtMs = time.GetUtcNow().ToUnixTimeMilliseconds() }, Open = open };
        store.SaveState(revoked);

        return revoked;
    }

    static void List(MuseAssistantStateStore store, string chatId) {
        var scope = store.LoadScope();
        if (!scope.Chats.Contains(chatId, StringComparer.Ordinal)) store.SaveScope(scope with { Chats = [.. scope.Chats, chatId] });
    }

    static void Unlist(MuseAssistantStateStore store, string chatId) {
        var scope = store.LoadScope();
        if (scope.Chats.Contains(chatId, StringComparer.Ordinal)) store.SaveScope(scope with { Chats = scope.Chats.Where(c => c != chatId).ToArray() });
    }

    /// <summary>The line the cron and the recording skill parse; the chat id is last because it is the one
    /// field that may hold a space.</summary>
    static string Describe(MuseAssistantChatState state) =>
        $"key={MuseAssistantPaths.ChatKey(state.ChatId)} watermark={state.Watermark?.ToString(CultureInfo.InvariantCulture) ?? "none"} {DescribeBoundary(state.Grant)} chat={state.ChatId}";

    static string DescribeBoundary(MuseAssistantGrant grant) =>
        $"boundary={grant.Boundary} after_seq={grant.AfterSeq?.ToString(CultureInfo.InvariantCulture) ?? "none"}";

    static int Fail(string verb, int code, string message) {
        Console.Error.WriteLine($"kcap muse-assistant {verb}: {message}");
        return code;
    }
}
