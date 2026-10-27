using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>Probes an account and commits what the vendor said. A probe takes a sequence number before it
/// runs and commits only when no later probe committed first, so refreshes finishing in reverse order never
/// restore an older login. Its limits belong to the login its own probe saw and commit with it.</summary>
public static class AccountRefresh {
    const int MaxRetiredSessions = 256;
    const int MaxRefreshOrigins  = 16;

    /// <summary>What the commit decided, carried from the registry change to the reading writes.</summary>
    sealed record Decision(long FromGeneration, long ToGeneration, bool MergeLimits);

    public static async Task<RefreshOutcome> RunAsync(AccountStore store, string accountId, IAccountProbe probe, TimeProvider time, CancellationToken ct) {
        var (started, startRevision) = store.MutateWithRevision(r => {
            if (r.Accounts.FirstOrDefault(a => a.Id == accountId) is not { } account) return (r, (VendorAccount?) null);

            var probing = account with { ProbeSeq = account.ProbeSeq + 1 };
            return (Replace(r, probing), probing);
        });

        if (started is null) return new(false, [], startRevision);

        var result = await probe.ProbeAsync(started, ct).ConfigureAwait(false);
        var now    = time.GetUtcNow();

        Decision? decision    = null;
        string?   writeFailed = null;

        var (found, revision) = store.MutateWithReadings(
            (r, held) => {
                if (r.Accounts.FirstOrDefault(a => a.Id == accountId) is not { } current) return (r, false);

                var (next, made) = Commit(current with { RefreshedAt = now }, started, result, held);
                decision = made;
                return (Replace(r, next), true);
            },
            (saved, held) => {
                if (decision is not { } d || saved.Accounts.FirstOrDefault(a => a.Id == accountId) is not { } account) return;

                try {
                    if (d.ToGeneration != d.FromGeneration) held.Delete(accountId);
                    if (d.MergeLimits && result.Limits is { } limits) {
                        var file = held.Read(accountId, account.Generation);
                        held.Write(accountId, file with {
                            Reading        = ReadingMerge.Apply(file.Reading, limits),
                            RefreshOrigins = [.. (file.RefreshOrigins ?? []).Append(limits.ProducedAt).TakeLast(MaxRefreshOrigins)],
                        });
                    }
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    writeFailed = ex.Message;
                }
            });

        return new(found, found ? Steps(started.Vendor, result, decision, writeFailed) : [], revision);
    }

    static (VendorAccount Next, Decision? Decision) Commit(VendorAccount current, VendorAccount started, ProbeResult result, HeldReadings held) {
        if (started.ProbeSeq <= current.CommittedProbeSeq) return (current, null);

        var next = current with {
            CommittedProbeSeq = started.ProbeSeq,
            RefreshError      = result.IdentityError,
            LimitsError       = current.Vendor is HarnessId.Codex ? result.LimitsError : null,
        };

        if (result.HookTrust is { } trust)
            next = next with { HookTrust = trust.Trust, HookTrustFingerprint = trust.Fingerprint };

        if (result.IdentityError is not null)
            return (next, new(current.Generation, current.Generation, current.Generation == started.Generation));

        if (result.SignIn is { } signIn) next = next with { SignIn = signIn };

        if (result.Identity is { } probed) {
            var recorded  = AccountIdentity.Of(current);
            var differs   = AccountIdentity.Differs(current.Vendor, recorded, probed);
            var committed = AccountIdentity.Commit(recorded, probed, differs);

            next = next with {
                Email           = committed.Email,
                OrgId           = committed.OrgId,
                OrgName         = committed.OrgName,
                Plan            = committed.Plan,
                VendorAccountId = committed.VendorAccountId,
                LabelSetByUser  = current.LabelIsUserSet,
            };

            if (!current.LabelIsUserSet && committed.Email is { } email) next = next with { Label = email };

            if (differs) {
                var seen    = held.Read(current.Id, current.Generation).Sessions.Select(s => s.SessionId);
                var retired = (current.RetiredSessions ?? []).Concat(seen).Distinct().TakeLast(MaxRetiredSessions);
                next = next with { Generation = current.Generation + 1, RetiredSessions = [.. retired] };
            }
        }

        return (next, new(current.Generation, next.Generation, true));
    }

    static List<WiringStep> Steps(HarnessId vendor, ProbeResult result, Decision? decision, string? writeFailed) {
        const string superseded = "superseded by a newer refresh";

        var steps = new List<WiringStep> {
            new("identity", result.IdentityError is null,
                result.IdentityError ?? (decision is null ? superseded : Describe(result))),
        };

        if (vendor is HarnessId.Codex) {
            var detail = result.LimitsError
                      ?? (writeFailed is not null ? $"could not write the reading: {writeFailed}"
                        : result.Limits is null   ? result.LimitsNote ?? "no limits reported"
                        : decision is null        ? superseded
                        : decision.MergeLimits    ? "recorded"
                                                  : "dropped: the account's login changed during the refresh");
            steps.Add(new("limits", result.LimitsError is null && writeFailed is null, detail));
        }

        return steps;
    }

    static string Describe(ProbeResult result) => result.SignIn switch {
        SignInStates.SignedIn  => result.Identity?.Email is { } email ? $"signed in as {email}" : "signed in",
        SignInStates.SignedOut => "signed out",
        _                      => "sign-in unknown",
    };

    static AccountRegistry Replace(AccountRegistry registry, VendorAccount account) =>
        registry with { Accounts = [.. registry.Accounts.Select(a => a.Id == account.Id ? account : a)] };
}
