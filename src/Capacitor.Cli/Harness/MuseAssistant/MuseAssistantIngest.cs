using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>
/// <c>kcap muse-assistant ingest &lt;dir&gt;</c>: one walk of one chat, written by Muse's cron. The chat lock
/// is held for the process lifetime; validation happens under it and before anything is posted; the
/// directory is never deleted here (the cron does that on exit 0).
/// </summary>
public sealed class MuseAssistantIngest(
        ConfigRoot                     config,
        UserHome                       home,
        MuseAssistantIdentity          identity,
        MuseAssistantStateStoreFactory stores,
        MuseAssistantActionRunner      runner,
        TimeProvider                   time
    ) {
    public async Task<int> RunAsync(string dir, CancellationToken ct) {
        if (MuseAssistantWalkReader.ReadChatId(dir) is not { } chatId)
            return Fail(MuseAssistantExitCodes.Malformed, $"{dir} is not a walk directory: manifest.json is missing or has no chat_id");

        var (principal, problem) = await identity.ResolveAsync(ct);
        if (principal is null) return Fail(MuseAssistantExitCodes.Server, problem!);

        var scopeDir = MuseAssistantPaths.ScopeDir(config, principal);
        var store    = stores.Create(scopeDir, principal);

        using var held = MuseAssistantChatLock.TryAcquire(MuseAssistantPaths.ChatLock(scopeDir, chatId));
        if (held is null) {
            Console.Error.WriteLine($"{time.GetUtcNow():O} another ingest holds the chat (or a scope command is changing it); nothing done");
            return MuseAssistantExitCodes.Ok;
        }

        var (walk, invalid) = MuseAssistantWalkReader.Load(dir);
        if (walk is null) return Fail(MuseAssistantExitCodes.Malformed, invalid!);
        if (walk.ChatId != chatId) return Fail(MuseAssistantExitCodes.Malformed, "manifest.json changed while it was being read");

        if (!store.LoadScope().Chats.Contains(chatId, StringComparer.Ordinal))
            return Fail(MuseAssistantExitCodes.OutOfScope, "this chat is not in scope; grant it with `kcap muse-assistant scope add --chat <id> --after-seq <seq>|--empty-chat|--include-history`");

        var (state, stateProblem) = store.LoadState(chatId);
        if (state is null)
            return Fail(MuseAssistantExitCodes.OutOfScope, stateProblem ?? "no grant is recorded for this chat; re-grant it with `kcap muse-assistant scope add`");

        if (state.Grant.Revoked) {
            if (await runner.ExecuteAsync(store, MuseAssistantPlanner.Closure(state), chatId, ct) is { } refused)
                return Fail(MuseAssistantExitCodes.Server, refused);

            return Fail(MuseAssistantExitCodes.OutOfScope, "recording for this chat was revoked; re-grant it with `kcap muse-assistant scope add`");
        }

        if (MuseAssistantPlanner.CoverageProblem(walk, state) is { } gap) return Fail(MuseAssistantExitCodes.Malformed, gap);

        if (MuseAssistantPlanner.OversizeProblem(walk, state) is { } oversize) return Fail(MuseAssistantExitCodes.Malformed, oversize);

        var actions = MuseAssistantPlanner.Plan(state, walk, principal.UserId, home.Path);

        return await runner.ExecuteAsync(store, actions, chatId, ct) is { } failure
            ? Fail(MuseAssistantExitCodes.Server, failure)
            : MuseAssistantExitCodes.Ok;
    }

    static int Fail(int code, string message) {
        Console.Error.WriteLine($"kcap muse-assistant ingest: {message}");
        return code;
    }
}
