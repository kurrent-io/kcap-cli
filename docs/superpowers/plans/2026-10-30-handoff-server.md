# Hand off an ended session: kcap-server (slice 2a of resume/hand-off) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the server everything a hand-off needs: the selected PR's head and the session's commits for the app, a hand-off launch the server refuses rather than degrades, the source context in the daemon command, and a lease-held reservation under which a daemon revalidates `Ended` and records the continuation.

**Architecture:** The app reads a new per-session `handoff-context` endpoint when the hand-off strip opens, and asks the hub for `GetLaunchFeatures` to learn whether the server knows hand-off at all. `RequestLaunchAgentV2` gains a trailing `Handoff` block; the hub refuses it below full access, for a non-owner requester, or to a daemon that did not advertise `SupportsHandoffLaunch`, and otherwise forwards the start plus a server-read source context in `LaunchAgentCommand.Handoff`. After launch the daemon calls `BeginHandoffTakeover` on its own hub connection: one step that checks the source is `Ended` from its stream, takes the in-memory `SourceSessionReservations` lease bound to that connection, and appends `SessionContinuedAs` guarded by the revision it read. The daemon renews the lease while it carries work over and ends it after; a dropped connection or a missed renewal frees it.

**Tech Stack:** .NET 10, ASP.NET Core SignalR, Eventuous/KurrentDB, TUnit + NSubstitute, the existing GitHub GraphQL client.

**Spec:** `docs/superpowers/specs/2026-10-29-resume-ended-sessions-design.md` in kcap-cli (Hand off; Constraints: capability negotiation, server data, server source reservation, launches go through the server).

Repository: `/Users/alexey/dev/eventstore/kcap-server`, its own branch and PR. Reference line `Part of kurrent-io/kcap-cli#2132 — AI-4757`. The CLI side is `2026-10-30-handoff-end-to-end.md`; it ships after this PR is deployed, and it must work (by refusing cleanly) against a server without it.

## Decisions this plan makes

- **Per-session read, not the listing.** The selected PR's head needs a live GitHub read; doing it for every listed row would cost one GraphQL call per session per page. The listing stays as it is; `GET /api/sessions/{id}/handoff-context` is read once when the strip opens. The recording-identity fields the spec also puts on the listing belong to slice 3.
- **Server capability is a hub method.** `GetLaunchFeatures()` returns `["handoff/1"]`. An older server has no such method, so the app's call fails and the app treats hand-off as unsupported. No feature flag and no version comparison.
- **`Ended` is decided from the stream, not the read model.** The projector can lag; a reactivating event appended after `SessionEnded` must refuse the takeover even if `sessions.status` still says ended. The begin step reads the root stream backwards to the first decisive event and appends `SessionContinuedAs` expecting that exact revision.
- **The reservation is in memory**, like `DaemonRegistry` and its launch reservations: one server process owns daemon connections, so a lease bound to a connection lives where the connection does.

## Global Constraints

- Every new wire field is trailing, optional and name-bound; absent means unsupported, never "assume capable".
- A hand-off launch is refused, never run as an ordinary launch, when: the daemon did not advertise `SupportsHandoffLaunch`; the requester is not the daemon's registered owner; the requester's access to the source is below `Full`; the source does not exist or is not visible.
- Reservation lease: TTL **60 s**, the daemon renews every **20 s**; released on `EndHandoffTakeover`, on the holder's disconnect, or on expiry. A takeover begin is refused while another holder's unexpired lease exists.
- `BeginHandoffTakeover` admits only a source whose latest decisive stream event is `SessionEnded` or `UserClosed`, that has no live agent (`AgentInstanceRegistry.FindLiveBySessionId` empty), and whose successor session belongs to an agent registered on the calling connection.
- PR selection: candidates are the session's linked PRs whose `repo_hash` equals the session's primary repo hash; an open one wins, then the most recent `updatedAt` from GitHub. A head repository is used only when it is the base repository or a verified fork of it (`isCrossRepository`, `headRepository.isFork`, `headRepository.parent.nameWithOwner` equal to the base); otherwise the context reports no usable PR head and the recorded branch is used.
- Comments follow kcap-cli CLAUDE.md's Comments rules (scarce, no history, no spec coordinates).

## Review Focus

1. A session reactivated after the daemon read it as ended but before the guarded append (a terminal `claude --resume` writes a `UserMessage`): the append must fail and the begin must refuse with `source_active`, not record the continuation. Pinned in Task 4.
2. A daemon that disconnects while holding the lease: a second begin from another connection must then be admitted without waiting for the TTL. Pinned in Task 3.
3. A session linked to PRs in two repositories, the other repository's PR more recently updated: the session's own repository's PR is selected. Pinned in Task 1.
4. GitHub unreachable or no token while reading the context: the endpoint still answers with branch, commits and the PR number from `session_prs`, with `head` null, rather than failing the strip. Pinned in Task 1.
5. A hand-off launch from an app whose server is new but whose daemon is old: refused with the coded reason, no `LaunchAgent` sent. Pinned in Task 2.

---

### Task 1: Hand-off context endpoint (selected PR head and session commits)

**Files:**
- Modify: `src/Capacitor.Server.Services/PullRequests/GitHubPullRequestQueries.cs` (new `HandoffHead` query)
- Create: `src/Capacitor.Server.Services/Sessions/Handoff/HandoffContextReader.cs`
- Create: `src/Capacitor.Api.Public.Abstractions/Sessions/HandoffContextDto.cs` (+ `HandoffPullRequestDto`)
- Modify: `src/Capacitor.Api.Public/Sessions/PublicSessionEndpoints.cs` (route), service registration beside `SessionCommitReader`'s
- Test: `test/Capacitor.Server.Services.Tests/Sessions/HandoffContextReaderTests.cs`, `test/Capacitor.Server.Tests.Read/ReadApi/HandoffContextEndpointTests.cs`

**Interfaces:**
- Produces: `GET /api/sessions/{sessionId}/handoff-context` → 200 `HandoffContextDto`, 404 when hidden or below `Full` (the same answer, so a lower level learns nothing).
- Produces: `public sealed record HandoffContextDto(string SessionId, string? RepoOwner, string? RepoName, string? Branch, HandoffPullRequestDto? PullRequest, IReadOnlyList<string>? SessionCommits)` — snake_case on the wire; `SessionCommits` null when the commit scan failed, empty when the session made none.
- Produces: `public sealed record HandoffPullRequestDto(int Number, string? State, string? HeadRef, string? HeadSha, string? HeadOwner, string? HeadRepo, bool? HeadIsFork)` — head fields null when GitHub could not be read; `HeadIsFork` true only for a verified fork, false for the base repository itself.
- Produces: `HandoffContextReader.ReadAsync(string sessionId, string viewerUserId, CancellationToken ct) : Task<HandoffContextDto?>` (null = hidden or below full).

- [ ] **Step 1: Write the failing reader tests** — `Selects_the_open_pr_of_the_sessions_own_repository` (two linked PRs, other repo's newer: expects the own-repo number), `Prefers_open_over_more_recently_updated_closed`, `A_head_in_an_unrelated_repository_is_not_used` (expects `HeadIsFork` null and head fields null, number kept), `A_verified_fork_reports_its_head` (`HeadOwner`/`HeadRepo`/`HeadSha` set, `HeadIsFork == true`), `GitHub_unreachable_keeps_branch_commits_and_number`, `Commits_are_limited_to_the_sessions_repository` (a commit whose owner/repo differ is dropped), `Below_full_access_reads_nothing`. Fake the GitHub client and `SessionCommitReader` the way `ForkBaseResolver`'s tests do.
- [ ] **Step 2: Run** `dotnet test --project test/Capacitor.Server.Services.Tests -- --treenode-filter "/*/*/HandoffContextReaderTests/*"`; expected: compile failure.
- [ ] **Step 3: Add `GitHubPullRequestQueries.HandoffHead`**: `pullRequest(number:$number) { number state updatedAt headRefName headRefOid isCrossRepository headRepository { nameWithOwner isFork parent { nameWithOwner } } }` plus `rateLimit`; the response type beside the overview's.
- [ ] **Step 4: Implement `HandoffContextReader`** — `SessionAccess.ReadAsync` gate (`Full` only); candidates from `session_prs` filtered on the session's primary repo hash; per candidate the `HandoffHead` read through the existing GitHub client (cached the way `PullRequestSnapshotStore` caches overviews); select open first, then `updatedAt` descending, falling back to `log_position` when GitHub is unreadable; commits from `SessionCommitReader.ReadAsync`, `Sha` only, filtered on owner/repo, null on exception.
- [ ] **Step 5: Run the reader tests**; expected: PASS.
- [ ] **Step 6: Write the endpoint test** in `HandoffContextEndpointTests` (KurrentDb fixture, seed via `RepoSessionsSeed`): full-access owner gets 200 with `branch` and `pull_request.number`; a viewer with `activity` access gets 404.
- [ ] **Step 7: Map the route** in `PublicSessionEndpoints` next to `/summary`; run both test classes; expected: PASS.
- [ ] **Step 8: Commit** — `Read a session's hand-off context with its selected PR head`

### Task 2: Hand-off launch fields, daemon capability and server feature list

**Files:**
- Create: `src/Capacitor.Api.Public.Abstractions/Agents/HandoffLaunchRequest.cs`, `src/Capacitor.Api.Public.Abstractions/Agents/Commands/HandoffLaunch.cs` (+ `HandoffSourceContext`)
- Modify: `RemoteLaunchRequest.cs` (trailing `Handoff`), `Commands/LaunchAgentCommand.cs` (trailing `Handoff` after `ExpectedRuntimeTransport`), `Daemon/DaemonConnectArgs.cs` (trailing `SupportsHandoffLaunch`)
- Modify: `src/Capacitor.Server.Services/Agents/DaemonRegistry.cs` (`DaemonEntry` field, `RegisterAsync` parameter, `SupportsHandoffLaunch(connId)` beside `SupportsBranchBaseRef`)
- Modify: `src/Capacitor.Api.Public/Sessions/CapacitorHub.cs` (`DaemonConnect` passes the flag; `RequestLaunchAgentCore`; new `GetLaunchFeatures`)
- Create: `src/Capacitor.Server.Services/Agents/HandoffLaunchAdmission.cs`
- Test: `test/Capacitor.Api.Public.Tests/Sessions/CapacitorHubHandoffLaunchTests.cs` (pattern: `CapacitorHubLaunchAttachmentAdmissionTests`)

**Interfaces:**
- Produces (app → server): `public sealed record HandoffLaunchRequest(string SourceSessionId, string StartSha, string Checkout, string? Branch, string? LocalBranchSha, string Source, string DetachReason, int? PullRequestNumber, bool HeadIsFork)` — `Checkout`/`Source`/`DetachReason` are the CLI enums' names (`CreateBranch`, `Branch`, `None`, …), passed through unread except for presence.
- Produces (server → daemon): `public sealed record HandoffLaunch(HandoffLaunchRequest Start, HandoffSourceContext Source, IReadOnlyList<string>? SessionCommits)`; `public sealed record HandoffSourceContext(string SessionId, string? Title, string? OwnerDisplay, string? RepoOwner, string? RepoName, string? Branch, int? PullRequestNumber, string? PullRequestTitle, string? FirstPrompt, string? LastPrompt)`.
- Produces: hub `public Task<string[]> GetLaunchFeatures()` → `["handoff/1"]`.
- Produces: `HandoffLaunchAdmission.AdmitAsync(HandoffLaunchRequest request, string requesterUserId, bool requesterIsOwner, bool daemonSupportsHandoff, CancellationToken ct) : Task<(HandoffLaunch? Launch, string? Refusal)>`; refusal codes `handoff_unsupported_daemon`, `handoff_not_owner`, `handoff_source_unavailable` (hidden, missing or below full), each with the human sentence after `: ` as the existing refusals do.

- [ ] **Step 1: Write the failing hub tests** — `A_handoff_to_a_daemon_without_the_capability_is_refused_and_nothing_is_sent` (asserts the refusal starts with `handoff_unsupported_daemon` and the client proxy received no `LaunchAgent`), `A_handoff_below_full_access_is_refused`, `A_handoff_for_someone_elses_daemon_is_refused`, `An_admitted_handoff_carries_the_start_and_the_source_context` (captured command's `Handoff.Start.StartSha`, `Handoff.Source.Title`, `Handoff.Source.LastPrompt`, `Handoff.SessionCommits`), `GetLaunchFeatures_lists_handoff`, and in the connect tests `DaemonConnect_records_SupportsHandoffLaunch` (absent → false).
- [ ] **Step 2: Run** the class; expected: compile failure.
- [ ] **Step 3: Add the records and trailing fields** (comments: one line each, what absent means).
- [ ] **Step 4: Implement `HandoffLaunchAdmission`** — reads the source with `SessionAccess.ReadAsync` under the requester; `Full` required; `HandoffSourceContext` from the summary (title, owner display, repo, branch, PR) plus first and last prompts from the same read the listing uses for `LastPrompt`; commits via `HandoffContextReader`'s commit step (extract it as `HandoffContextReader.ReadCommitsAsync(sessionId, owner, repo, ct)`).
- [ ] **Step 5: Wire it in `RequestLaunchAgentCore`** after `DaemonLaunchCapabilityPolicy` and before `TryReserveAsync`, so a refusal reserves nothing; set `LaunchAgentCommand.Handoff`. Add `GetLaunchFeatures`.
- [ ] **Step 6: Run the hub tests and the existing `Capacitor.Api.Public.Tests` suite**; expected: PASS.
- [ ] **Step 7: Commit** — `Refuse a hand-off launch the daemon or requester cannot carry`

### Task 3: Source session reservations

**Files:**
- Create: `src/Capacitor.Server.Services/Sessions/Handoff/SourceSessionReservations.cs`
- Modify: `src/Capacitor.Api.Public/Sessions/CapacitorHub.cs` (`OnDisconnectedAsync`: release by connection, in its own try/catch like its siblings); DI registration as a singleton
- Test: `test/Capacitor.Server.Services.Tests/Sessions/SourceSessionReservationsTests.cs`

**Interfaces:**
- Produces: `public sealed class SourceSessionReservations(TimeProvider time)` with
  - `public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);`
  - `public SourceReservation? TryAcquire(string sourceSessionId, string holderConnectionId, SourceReservationKind kind)` — null while another unexpired lease exists;
  - `public bool Renew(string token, string holderConnectionId)`; `public void Release(string token, string holderConnectionId)`; `public void ReleaseConnection(string connectionId)`.
- Produces: `public enum SourceReservationKind { Takeover, Resume }`; `public sealed record SourceReservation(string Token, string SourceSessionId, SourceReservationKind Kind, DateTimeOffset ExpiresAt)`.

- [ ] **Step 1: Write the failing tests** (`FakeTimeProvider`): `A_second_holder_is_refused_while_the_lease_is_live`, `An_expired_lease_is_taken_over`, `Renew_extends_the_lease`, `Renew_from_another_connection_fails`, `ReleaseConnection_frees_every_lease_it_held`, `Release_with_a_stale_token_frees_nothing` (a lease re-acquired after expiry survives the old holder's release), `Session_ids_are_compared_canonically` (dashed and dashless spellings collide).
- [ ] **Step 2: Run**; expected: compile failure.
- [ ] **Step 3: Implement** with one lock over a dictionary keyed by `CanonicalSessionId.Normalize(source)`; expiry checked on every call, no timer.
- [ ] **Step 4: Run**; expected: PASS. Add `OnDisconnectedAsync` release and a hub test `A_daemon_disconnect_releases_its_takeover_lease` in `CapacitorHubHandoffTakeoverTests` (created in Task 4; write it there).
- [ ] **Step 5: Commit** — `Hold a lease on a source session while it is taken over`

### Task 4: Begin, renew and end a hand-off takeover

**Files:**
- Create: `src/Capacitor.Server.Services/Sessions/Handoff/HandoffTakeoverGate.cs`
- Create: `src/Capacitor.Api.Public.Abstractions/Agents/Daemon/HandoffTakeoverAdmission.cs`
- Modify: `src/Capacitor.Server.Services/Sessions/SessionWriter.Writes.cs` (a read of the root stream's latest decisive end-or-reactivation event and its revision)
- Modify: `CapacitorHub.cs` (three daemon-side hub methods)
- Test: `test/Capacitor.Server.Services.Tests/Sessions/HandoffTakeoverGateTests.cs` (KurrentDb fixture, like `SessionWriterContinuationTests`), `test/Capacitor.Api.Public.Tests/Sessions/CapacitorHubHandoffTakeoverTests.cs`

**Interfaces:**
- Consumes: `SourceSessionReservations` (Task 3), `SessionWriter.TryRecordSessionContinuedAsAtAsync(source, next, expectedRevision, ct)`, `SessionSummaryFold.IsReactivationEvent`, `AgentInstanceRegistry.FindLiveBySessionId`.
- Produces: `public sealed record HandoffTakeoverAdmission(string? Token, string? Refusal, int LeaseSeconds)` — exactly one of `Token`/`Refusal`; refusal codes `source_active`, `source_reserved`, `source_unavailable`, `successor_unknown`.
- Produces hub methods (daemon connection only; a caller without a registered daemon entry gets `source_unavailable`):
  - `Task<HandoffTakeoverAdmission> BeginHandoffTakeover(string sourceSessionId, string successorSessionId)`
  - `Task<bool> RenewHandoffTakeover(string token)`
  - `Task EndHandoffTakeover(string token)`
- Produces: `SessionWriter.ReadEndStateAsync(string sessionId, CancellationToken ct) : Task<(bool Ended, long Revision)?>` — null when the stream does not exist.
- Produces: `HandoffTakeoverGate.BeginAsync(string source, string successor, string connectionId, string ownerUserId, CancellationToken ct) : Task<HandoffTakeoverAdmission>`.

- [ ] **Step 1: Write the failing gate tests** — `An_ended_source_is_admitted_and_records_the_continuation` (projected `next_session_id` equals the successor), `A_source_reactivated_after_its_end_is_refused` (append `SessionEnded` then `UserMessage`: `source_active`), `A_reactivation_between_read_and_append_is_refused` (inject an append after `ReadEndStateAsync` returns, via a hook on the gate; expects `source_active` and no `SessionContinuedAs` on the stream), `A_live_agent_on_the_source_is_refused`, `A_held_lease_is_refused_as_source_reserved`, `A_successor_not_on_the_calling_connection_is_refused`, `Below_full_access_is_source_unavailable`, `A_refused_begin_releases_the_lease_it_took` (a second begin after a `source_active` refusal is not `source_reserved`).
- [ ] **Step 2: Run**; expected: compile failure.
- [ ] **Step 3: Implement `ReadEndStateAsync`** — read the root stream backwards; the first event that is `SessionEnded`/`UserClosed` gives `Ended = true`, the first for which `IsReactivationEvent` holds gives `false`; revision is the stream's last revision, not the decisive event's.
- [ ] **Step 4: Implement `BeginAsync`** in this order: access (`Full` for `ownerUserId`) → successor registered on `connectionId` → `TryAcquire` → `ReadEndStateAsync` → live agent check → `TryRecordSessionContinuedAsAtAsync` at the read revision. Any refusal after `TryAcquire` releases the lease before returning.
- [ ] **Step 5: Run the gate tests**; expected: PASS.
- [ ] **Step 6: Write the hub tests** — `Begin_from_a_non_daemon_connection_is_refused`, `Renew_and_end_round_trip`, and Task 3's `A_daemon_disconnect_releases_its_takeover_lease`. Implement the hub methods (owner from `daemonRegistry` by `Context.ConnectionId`). Run; expected: PASS.
- [ ] **Step 7: Run the full server unit suites touched** (`Capacitor.Server.Services.Tests`, `Capacitor.Api.Public.Tests`, `Capacitor.Server.Tests.Read`); expected: PASS.
- [ ] **Step 8: Commit** — `Revalidate an ended source under a lease before a hand-off takeover`

### Task 5: Pull request

- [ ] Bump nothing in `src/cli` here (the CLI PR lands after; its models are independent copies).
- [ ] Open the PR per kcap-server's template, reference line `Part of kurrent-io/kcap-cli#2132 — AI-4757`. Ask the user before merging.
