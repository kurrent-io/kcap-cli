# Past and unhosted sessions in the rail (slice 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The desktop rail lists every session the server lets the signed-in user see, live or ended, hosted or started in a terminal, with scope, state, grouping and sort controls remembered per user, paged twenty ended rows at a time, and a server row opens read-only in the existing chat workspace with a lease that follows the session

**Architecture:** A third row lane, `server:{sessionId}`, is fed by a new `ServerSessionsService` over `api/sessions/listing` and `api/sessions/owners`, reconciled on hub pushes, reconnect and a slow cadence, and merged in `AgentDirectory` with the two daemon lanes under one-session-one-row rules decided by current evidence. `SessionRailViewModel` gains the four controls, a switchable grouping and sort over the same DynamicData source, a state filter with a selected-row exemption, and a bounded ended prefix with Show 20 more. `RemoteSessionViewModel` opens a session-only row with no Stop, Terminal or composer, acquiring its access lease whatever the session's status, and `MainWindowViewModel` hands a finished daemon row's workspace off to the server row by session id

**Tech Stack:** .NET 10, Avalonia, ReactiveUI, DynamicData, System.Text.Json source generation (AOT), TUnit, WireMock.Net

**Spec:** `docs/superpowers/specs/2026-10-24-desktop-past-sessions-design.md` (decisions D1, D2, D3, D5, D8, D9 and the slice 1 line; D4's Overview, D6 and D7 are later slices)

## Global Constraints

- Every new JSON type is registered in `RemoteModelsJsonContext` (or `AppStateJsonContext`); no reflection serialization, no `JsonArray` collection expressions (AOT)
- New parameters go before the last one unless they are optional; optional parameters append
- One type per file, named after the type; enums beside their extension methods are the only pairing allowed
- Comments: scarce, present-tense, no history, no ticket ids, no review artefacts; `Capacitor.App/AGENTS.md` and the repository `CLAUDE.md` rules apply
- Status palette is status only: owner initials, vendor marks, repository names and the terminal chip use text, muted and surface tokens; `KcapPurple*` is in-flight, `KcapInfo*`/`KcapSelectionBrush` is selection
- The rail keeps one `ObserveOn(RxSchedulers.MainThreadScheduler)` at the top of its pipeline; regrouping and resorting are operators over the same source
- No request on a hot path: listing reads run on connect, on a push (debounced), on the 60-second cadence and on Show 20 more; never inside a recompute
- Server rows never clobber another lane's key; suppression is computed in `AgentDirectory.Recompute` and nowhere else
- Tests follow the repository's conventions: TUnit, `TempDir`, `[NotInParallel("AvaloniaSession")]` for anything touching the UI scheduler, no XAML-only smoke tests, `FakeServerLane`/`FakeRemoteAgents`/`FakeAgentDirectory`/`FakeDaemonClientService` as the lane fakes
- Copy: "Show 20 more", "12 of 212" (loaded of total), "Search or narrow the scope for older sessions", "N more live sessions · narrow the scope", "Running in a terminal · read-only here: answer it where it runs", group labels "Waiting on you", "Running", "Failed", "Ended", "Today", "Yesterday", "No repository", tab label "Pages"
- Default preferences: Mine · All · Repository · Last updated · descending · nothing collapsed
- Constants: page size 20, live page size 100, offset cap 500, grace 30 s, reconcile cadence 60 s, push debounce 250 ms, ended-row PR cadence 10 min

## Review Focus

1. A listing row with `repo` null and `cwd` null (an imported session with no checkout): it must group under "No repository" and open, not throw on an empty path (Task 5 pins it)
2. Two server rows whose session ids differ only in case (the server canonicalises GUIDs without dashes; a hook may spell one differently): the directory must key by the string the server returns and never merge two keys (Task 6 pins it)
3. The signed-in account's `Ids` list arriving after the live listing: a teammate's row shown under Mine for a moment must move to Team when the ids land, not stay (Task 6 pins it)
4. The local day rolling over at midnight while the rail is open under Date grouping: yesterday's rows must regroup on the next minute tick without a scope change (Task 8 pins it)
5. A `ShowMore` click while a reconciliation is in flight: the two must not interleave into a prefix with a hole or a duplicate (Task 4 pins it)

---

### Task 1: Listing and owners wire types

**Files:**
- Create: `src/Capacitor.Remote.Models/RepoSessionDto.cs`, `RepoSessionOwnerDto.cs`, `RepoSessionRepositoryDto.cs`, `RepoSessionsResponseDto.cs`, `SessionOwnerOptionDto.cs`
- Modify: `src/Capacitor.Remote.Models/RemoteWire.cs` (`ApiRoutes`), `src/Capacitor.Remote.Models/RemoteModelsJsonContext.cs`
- Test: `test/Capacitor.App.Tests.Unit/RepoSessionDtoJsonTests.cs`

**Interfaces:**
- Produces: `RepoSessionDto { string SessionId; string? Slug; string? Title; RepoSessionOwnerDto? Owner; string? Vendor; string Status; string AccessLevel; bool Stale; DateTimeOffset StartedAt; DateTimeOffset? EndedAt; DateTimeOffset LastActivityAt; string? PrimaryRepoHash; bool IsPrimary; string? Branch; string? Cwd; string? LastPrompt; RepoSessionRepositoryDto? Repo }` with `[JsonPropertyName]` snake_case on every member (`session_id`, `access_level`, `started_at`, `ended_at`, `last_activity_at`, `primary_repo_hash`, `is_primary`, `last_prompt`)
- `RepoSessionOwnerDto { string UserId; string? Username; string? DisplayName; string? AvatarUrl }` (`user_id`, `display_name`, `avatar_url`)
- `RepoSessionRepositoryDto { string Hash; string? Owner; string? Name }`
- `RepoSessionsResponseDto { RepoSessionDto[] Items; int Total; int Limit; int Offset; string? NextCursor }` (`next_cursor`)
- `SessionOwnerOptionDto { string UserId; string? Username; string? DisplayName; string? AvatarUrl; bool IsMe; string[]? Ids }` (`is_me`)
- `ApiRoutes.SessionListing(string state, string? owner, int limit, int offset)` returning `api/sessions/listing?state={state}&limit={limit}&offset={offset}` plus `&owner={Uri.EscapeDataString(owner)}` when owner is not null; `ApiRoutes.SessionOwners = "api/sessions/owners"`
- `RemoteModelsJsonContext` gains `[JsonSerializable(typeof(RepoSessionsResponseDto))]` and `[JsonSerializable(typeof(SessionOwnerOptionDto[]))]`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task Listing_response_deserializes_snake_case_rows() {
    const string json = """
        {"items":[{"session_id":"d0ac235c09644610a297b7671e40e1a8","slug":null,"title":"Desktop app RAM growth",
          "owner":{"user_id":"user_01","username":"alexey","display_name":"Alexey Zimarev","avatar_url":null},
          "vendor":"claude","status":"ended","access_level":"full","stale":false,
          "started_at":"2026-10-22T13:04:24.445109+00:00","ended_at":"2026-10-22T16:56:41.708815+00:00",
          "last_activity_at":"2026-10-22T16:56:41.708815+00:00","primary_repo_hash":"830e1690b97dd8c0","is_primary":true,
          "branch":"fix/terminal-replay-loh","cwd":"/Users/alexey/dev/kcap-cli","last_prompt":"(2) is done",
          "write_attempt_paths":[],"write_attempt_count":0,"repo":{"hash":"830e1690b97dd8c0","owner":"kurrent-io","name":"kcap-cli"}}],
         "total":212,"limit":20,"offset":0,"since":null,"until":null,"next_cursor":null}
        """;
    var page = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.RepoSessionsResponseDto)!;
    await Assert.That(page.Total).IsEqualTo(212);
    var row = page.Items.Single();
    await Assert.That(row.SessionId).IsEqualTo("d0ac235c09644610a297b7671e40e1a8");
    await Assert.That(row.Owner!.DisplayName).IsEqualTo("Alexey Zimarev");
    await Assert.That(row.Status).IsEqualTo("ended");
    await Assert.That(row.EndedAt).IsEqualTo(DateTimeOffset.Parse("2026-10-22T16:56:41.708815+00:00"));
    await Assert.That(row.Repo!.Name).IsEqualTo("kcap-cli");
}

[Test]
public async Task Owners_deserialize_with_is_me_and_ids() {
    const string json = """[{"user_id":"user_01","username":"alexey","display_name":"Alexey","avatar_url":null,"is_me":true,"ids":["user_01","github:2821205"]}]""";
    var owners = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.SessionOwnerOptionDtoArray)!;
    await Assert.That(owners[0].IsMe).IsTrue();
    await Assert.That(owners[0].Ids).IsEquivalentTo(["user_01", "github:2821205"]);
}

[Test]
public async Task Listing_route_carries_owner_only_when_given() {
    await Assert.That(ApiRoutes.SessionListing("ended", "me", 20, 40)).IsEqualTo("api/sessions/listing?state=ended&limit=20&offset=40&owner=me");
    await Assert.That(ApiRoutes.SessionListing("active", null, 100, 0)).IsEqualTo("api/sessions/listing?state=active&limit=100&offset=0");
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RepoSessionDtoJsonTests/*"`
Expected: build failure, `RepoSessionsResponseDto` not defined

- [ ] **Step 3: Add the five records, the two routes and the context registrations**

Records are `sealed record` with `{ get; init; }` members and `[JsonPropertyName]` on each, as `AgentInstanceDto` does; `required` only on `SessionId`, `Status`, `AccessLevel`, `StartedAt`, `LastActivityAt`, `Items`, `UserId`

- [ ] **Step 4: Run the test to verify it passes**

Same command. Expected: 3 passed

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Remote.Models test/Capacitor.App.Tests.Unit/RepoSessionDtoJsonTests.cs
git commit -m "Add the session listing and owners wire types (#1910)"
```

---

### Task 2: Session pushes on the server lane

**Files:**
- Create: `src/Capacitor.App/Services/SessionPush.cs`, `src/Capacitor.App/Services/SessionPushKind.cs`
- Modify: `src/Capacitor.App/Services/IServerLane.cs`, `src/Capacitor.App/Services/ServerConnectionService.cs` (bind the pushes, drop them from `UnusedPings`), `src/Capacitor.App/Services/NoRemoteAgents.cs` (`NoServerLane`), `test/Capacitor.App.Tests.Unit/FakeServerLane.cs`
- Test: none of its own; Task 4's tests drive the fake's subject, and the hub binding is exercised by the existing connection-service tests compiling against the new member

**Interfaces:**
- Produces: `enum SessionPushKind { ActiveAdded, ActiveChanged, ActiveRemoved, TitleChanged, Deleted }`; `sealed record SessionPush(SessionPushKind Kind, string SessionId)`; `IServerLane.SessionPushes : IObservable<SessionPush>`; `FakeServerLane.SessionPushesSubject : Subject<SessionPush>`
- The server sends each of `ActiveSessionAdded`, `ActiveSessionChanged`, `ActiveSessionRemoved`, `SessionTitleChanged`, `SessionDeleted` with one string argument, the session id

- [ ] **Step 1: Add the two types and the interface member; make `NoServerLane` return `Observable.Never<SessionPush>()` and `FakeServerLane` expose a `Subject<SessionPush>`**

- [ ] **Step 2: In `ServerConnectionService.BuildHub`, replace the five names in `UnusedPings` with `hub.On<string>(HubBroadcasts.X, id => _sessionPushes.OnNext(new(SessionPushKind.Y, id)))` bindings over a `Subject<SessionPush> _sessionPushes`, exposed as `SessionPushes`**

- [ ] **Step 3: Build the app and the test project**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: success, no IDE0005

- [ ] **Step 4: Commit**

```bash
git add src/Capacitor.App/Services test/Capacitor.App.Tests.Unit/FakeServerLane.cs
git commit -m "Surface the session pushes on the server lane (#1910)"
```

---

### Task 3: Registry freshness evidence

**Files:**
- Modify: `src/Capacitor.App/Services/RemoteAgentsService.cs` (interface and class), `src/Capacitor.App/Services/NoRemoteAgents.cs`, `test/Capacitor.App.Tests.Unit/FakeRemoteAgents.cs`
- Test: `test/Capacitor.App.Tests.Unit/RemoteAgentsServiceTests.cs`

**Interfaces:**
- Produces: `IRemoteAgentsService.Current : IObservable<bool>` (replay-1, initial false). True once a fetch under the current generation returns rows (empty or not); false when the lane's status leaves Connected and when a fetch returns null rows. `FakeRemoteAgents.CurrentSubject : BehaviorSubject<bool>` initial true (existing tests read retained rows as current); `NoRemoteAgents.Current` returns false

- [ ] **Step 1: Write the failing tests**

```csharp
[Test]
public async Task Current_is_true_after_a_successful_fetch_and_false_after_a_failed_one() {
    var lane = new FakeServerLane();
    var answers = new Queue<RemoteFetch>([new RemoteFetch([]), new RemoteFetch(null)]);
    using var service = new RemoteAgentsService(lane, _ => Task.FromResult(answers.Dequeue()), TimeSpan.Zero);
    var seen = new List<bool>();
    using var _ = service.Current.Subscribe(seen.Add);
    lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
    await WaitUntilAsync(() => seen.Contains(true), what: "current after first fetch");
    lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
    await WaitUntilAsync(() => seen[^1] == false, what: "stale after failed fetch");
}

[Test]
public async Task Current_drops_when_the_lane_leaves_connected() {
    var lane = new FakeServerLane();
    using var service = new RemoteAgentsService(lane, _ => Task.FromResult(new RemoteFetch([])), TimeSpan.Zero);
    var seen = new List<bool>();
    using var _ = service.Current.Subscribe(seen.Add);
    lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
    await WaitUntilAsync(() => seen.Contains(true), what: "current");
    lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
    await Assert.That(seen[^1]).IsFalse();
}
```

(`WaitUntilAsync` is `WorkspaceFixtures.WaitUntilAsync`, imported with `using static`)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteAgentsServiceTests/*"`
Expected: build failure, `Current` not defined

- [ ] **Step 3: Implement `Current` as a `BehaviorSubject<bool>` set inside the same locked completion block that publishes rows (true when `result?.Rows is not null`, false otherwise) and set false in the status subscription whenever `s.State != Connected`**

- [ ] **Step 4: Run to verify they pass; run the whole class**

Same command. Expected: all pass

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services test/Capacitor.App.Tests.Unit
git commit -m "Report whether the registry rows are current (#1910)"
```

---

### Task 4: The server sessions service

**Files:**
- Create: `src/Capacitor.App/Services/RailScope.cs` (`enum RailScope { Mine, Team }`), `src/Capacitor.App/Services/IServerSessionsService.cs`, `src/Capacitor.App/Services/ServerSessionsService.cs`, `src/Capacitor.App/Services/ServerSessionsQuery.cs`, `src/Capacitor.App/Services/ServerSessionsFetch.cs`, `src/Capacitor.App/Services/ServerSessionsPaging.cs`
- Test: `test/Capacitor.App.Tests.Unit/ServerSessionsServiceTests.cs`, `test/Capacitor.App.Tests.Unit/ServerSessionsServiceHttpFetchTests.cs`, `test/Capacitor.App.Tests.Unit/FakeServerSessionsService.cs` (for Tasks 6 and 8)

**Interfaces:**
- Consumes: `IServerLane.Status`, `IServerLane.SessionPushes` (Task 2); `RepoSessionDto`, `RepoSessionsResponseDto`, `SessionOwnerOptionDto`, `ApiRoutes` (Task 1)
- Produces:
  - `sealed record ServerSessionsQuery(string State, string? Owner, int Limit, int Offset)`
  - `sealed record ServerSessionsFetch(RepoSessionDto[]? Rows, int Total, bool Unauthorized = false)`; `Rows` null on any failure
  - `sealed record ServerSessionsPaging(int Loaded, int Total, int Pages, bool CanShowMore, bool AtCap)`
  - `interface IServerSessionsService { IObservableCache<RepoSessionDto,string> Live; IObservableCache<RepoSessionDto,string> MineLive; IObservableCache<RepoSessionDto,string> Ended; IObservable<ServerSessionsPaging> Paging; IObservable<int> LiveNotShown; IObservable<IReadOnlySet<string>> ViewerIds; IObservable<string?> ViewerUserId; IObservable<bool> Current; RailScope Scope { get; set; } IObservable<RailScope> ScopeChanges; Task ShowMoreAsync(); Task ReconcileAsync(); }` — every cache keyed by `SessionId`; every observable replay-1 on the thread it was produced on (consumers marshal)
  - `ServerSessionsService(IServerLane lane, Func<ServerSessionsQuery, CancellationToken, Task<ServerSessionsFetch>> fetch, Func<CancellationToken, Task<SessionOwnerOptionDto[]?>> fetchOwners, TimeProvider time, TimeSpan? pushDebounce = null, TimeSpan? reconcileEvery = null, Action<int>? onUnauthorized = null)`; constants `PageSize = 20`, `LivePageSize = 100`, `OffsetCap = 500`
  - `ServerSessionsService.HttpFetch(ICapacitorHttpClient? http, ProfileContext? profiles)` and `HttpOwners(...)` static factories shaped like `RemoteAgentsService.HttpFetch`
  - `FakeServerSessionsService : IServerSessionsService` with public `SourceCache`s and `BehaviorSubject`s for every member and recorded `ShowMoreCalls`/`ReconcileCalls`
- Behaviour pinned by the tests below:
  - `Live` is the unfiltered active listing read in pages of `LivePageSize` while `offset <= OffsetCap`; `LiveNotShown = total - read` for the scope's row source
  - `MineLive` is the `owner=me` active listing, maintained only while `Scope == Mine`, cleared on switching to Team
  - `Ended` holds the newest `N = PageSize * Pages` rows of `state=ended` for the scope (`owner=me` under Mine), `Pages` starting at 1; `ShowMoreAsync` increments `Pages` and reads the next page; `AtCap` when `Pages * PageSize >= OffsetCap`
  - `ReconcileAsync` re-reads the live listing(s) and the whole prefix (`Pages` requests) and replaces `Ended` with the result; it runs on Connected, on every debounced push, and every `reconcileEvery`; `ActiveRemoved` and `Deleted` pushes count as a push; `Deleted` also removes the row at once
  - a scope change resets `Pages` to 1, clears `Ended` and reconciles
  - generation: bumps on each Connected status with a subject; a completion from an older generation publishes nothing; a subject change clears `Live`, `MineLive`, `Ended`, `ViewerIds` before the first new read
  - `Current` true after any successful read in this connection, false on leaving Connected or a failed read
  - `ViewerIds` from the owners read's `IsMe` row (`Ids`, else `[UserId]`); `ViewerUserId` its `UserId`; both read on Connected
  - one reconciliation at a time: a reconcile or ShowMore admitted while one is running reruns after it (the `RemoteAgentsService` busy/rerun pattern), so the prefix is always one read's result

- [ ] **Step 1: Write the failing tests** (scripted fetch keyed by `(State, Owner, Offset)`; `FakeTimeProvider` for cadence)

```csharp
[Test] public async Task Connected_reads_live_and_first_ended_page_for_mine()
  // scope Mine: fetch sees ("active","me",100,0), ("active",null,100,0), ("ended","me",20,0); Ended has 20 rows; Paging == (20, 212, 1, true, false)
[Test] public async Task Show_more_appends_the_next_page_and_reconcile_rereads_the_whole_prefix()
  // ShowMore → ("ended","me",20,20); Paging.Loaded == 40; then a push ActiveRemoved → fetch sees offsets 0 and 20 again; Ended replaced, not merged: a row the second read no longer returns is gone
[Test] public async Task A_burst_between_reconciliations_leaves_no_hole()
  // pages=2 loaded (40 rows s1..s40); script 30 new rows n1..n30 ahead; reconcile → Ended == n1..n30,s1..s10 exactly
[Test] public async Task At_cap_show_more_is_refused()
  // Pages = 25 → AtCap true, CanShowMore false, ShowMoreAsync issues no fetch
[Test] public async Task Team_scope_reads_one_live_listing_and_unfiltered_ended_pages()
  // Scope = Team: no ("active","me",…) fetch; ("ended",null,20,0); MineLive empty
[Test] public async Task Live_beyond_the_cap_reports_the_remainder()
  // active total 700, pages of 100 up to offset 500 → Live.Count == 600, LiveNotShown == 100
[Test] public async Task A_subject_change_clears_every_cache_before_the_new_read()
  // Connected(u1) populates; Connected(u2) with the fetch gated: Live/MineLive/Ended/ViewerIds empty while gated; after release they hold u2's rows; a late u1 completion publishes nothing
[Test] public async Task Current_follows_reads_and_the_lane()
  // true after connect; Retrying → false; Connected + failed fetch → false
[Test] public async Task Owners_resolve_the_viewer_ids()
  // owners [{is_me:true, ids:[a,b]}] → ViewerIds == {a,b}, ViewerUserId == a
[Test] public async Task Show_more_during_a_reconcile_waits_and_runs_once_after()
  // gate the reconcile's first fetch; call ShowMoreAsync; release: the ShowMore read happens after the reconcile completes and Ended ends with 40 distinct rows
[Test] public async Task Deleted_push_removes_the_row_at_once()
```

And in `ServerSessionsServiceHttpFetchTests` (WireMock, as `RemoteAgentsServiceHttpFetchTests`):

```csharp
[Test] public async Task Fetch_reads_the_listing_and_maps_401_to_unauthorized()
  // path /api/sessions/listing with query state=ended&limit=20&offset=0&owner=me → Rows[0].SessionId, Total; 401 → Rows null, Unauthorized true
[Test] public async Task Owners_fetch_reads_the_owners_route()
```

- [ ] **Step 2: Run to verify they fail** (build failure: types missing)

- [ ] **Step 3: Implement the records, the interface, `ServerSessionsService` and the fake**

Admission and completion share one `Lock`, as `RemoteAgentsService` does; the reconcile loop reads live (scope read first, discovery read second under Mine), then `Pages` ended pages in order, and publishes with `EditDiff` under the generation check. Push debounce via `Throttle(pushDebounce ?? 250 ms)` on `lane.SessionPushes`; cadence via `time.CreateTimer`. HTTP factories use `ForWaitAsync`, map 401 to `Unauthorized`, read with `RemoteModelsJsonContext.Default.RepoSessionsResponseDto`

- [ ] **Step 4: Run the two classes; expected all pass**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services test/Capacitor.App.Tests.Unit
git commit -m "Read the server's session listing into a reconciled lane (#1910)"
```

---

### Task 5: Server rows

**Files:**
- Modify: `src/Capacitor.App/Services/AgentRow.cs`
- Test: `test/Capacitor.App.Tests.Unit/AgentRowTests.cs` (new)

**Interfaces:**
- Produces: `AgentOrigin` gains `Server` as its last member (ordering: a session claimed on more than one lane resolves to the lowest)
- `AgentRow` gains trailing optional members: `string? OwnerUserId = null`, `string? OwnerName = null`, `bool? OwnerIsViewer = null`, `DateTime? LastActivityAt = null`, `DateTime? EndedAt = null`, `bool IsStale = false`, `bool IsTerminalSession = false`
- `AgentRow.FromRemote` sets `OwnerUserId = dto.OwnerUserId`
- `AgentRow.FromServer(RepoSessionDto dto, bool hostedPresentation)`: `Key = $"server:{dto.SessionId}"`, `Origin = Server`, `Id = dto.SessionId`, `SessionId = dto.SessionId`, `Kind = "session"`, `Vendor = dto.Vendor ?? ""`, `Status = dto.Status == "active" ? "Running" : "Completed"`, `CreatedAt = dto.StartedAt.UtcDateTime`, `RepoPath = dto.Cwd`, `Title = dto.Title`, `Model = null`, `MachineBadge = null`, repo identity `RepoIdentityResolver.ForRemote(dto.Repo?.Owner, dto.Repo?.Name, dto.Cwd, "server")`, `CheckoutKey = "@server"`, `CheckoutLabel = ""`, `AwaitingInput = null`, `OwnerUserId = dto.Owner?.UserId`, `OwnerName = dto.Owner?.DisplayName ?? dto.Owner?.Username`, `LastActivityAt = dto.LastActivityAt.UtcDateTime`, `EndedAt = dto.EndedAt?.UtcDateTime`, `IsStale = dto.Stale`, `IsTerminalSession = !hostedPresentation && dto.Status == "active"`
- `AgentRow.IsEndedSession => Origin == AgentOrigin.Server && Status == "Completed"` (expression-bodied property)

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task Server_row_maps_an_ended_listing_row()
  // ended dto with repo kurrent-io/kcap-cli → Key "server:s1", Origin Server, Status "Completed", RepoGroupKey "repo:kurrent-io/kcap-cli", RepoGroupLabel "kurrent-io/kcap-cli", OwnerName "Alexey Zimarev", EndedAt set, IsEndedSession true, IsTerminalSession false
[Test] public async Task Server_row_without_repo_or_cwd_groups_under_no_repository()
  // repo null, cwd null → RepoGroupLabel "No repository", RepoGroupKey "daemon:server:", no exception
[Test] public async Task Live_server_row_is_a_terminal_session_unless_hosted()
  // active, hostedPresentation false → IsTerminalSession true, Status "Running"; hostedPresentation true → false
[Test] public async Task Remote_row_carries_the_registry_owner()
```

- [ ] **Step 2: Run to verify they fail**; **Step 3: Implement**; **Step 4: Run to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentRowTests/*"`

- [ ] **Step 5: Commit** — `git commit -m "Project a listing row onto the rail's row type (#1910)"`

---

### Task 6: The directory's server lane

**Files:**
- Modify: `src/Capacitor.App/Services/AgentDirectory.cs`
- Test: `test/Capacitor.App.Tests.Unit/AgentDirectoryTests.cs`

**Interfaces:**
- Consumes: `IServerSessionsService` (Task 4), `IRemoteAgentsService.Current` (Task 3), `AgentRow.FromServer` (Task 5)
- Produces: `AgentDirectory(IDaemonClientService local, IRemoteAgentsService remote, IServerLane lane, RepoIdentityResolver repoIdentity, Func<string,string> resolveLocalRepoRoot, string? localMachineId, string? appServerUrl, TimeProvider time, IServerSessionsService? sessions = null)`; constant `AgentDirectory.HandoverGrace = TimeSpan.FromSeconds(30)`
- Recompute rules, with `S` a session id:
  1. Server rows come from `sessions.Ended` plus the scope's live source (`MineLive` under Mine, `Live` under Team); each is `FromServer(dto, hostedPresentation)` where `hostedPresentation` is true while a daemon row for `S` is suppressing it or did so within `HandoverGrace` (a `Dictionary<string, DateTime>` of last-suppressing times, swept by a timer like `_placeholderExpiry`)
  2. A local row is **live** when `_localConnected` and its status is `Starting`, `Running`, `Idle`, `Resuming` or `Interrupted`, or when `WaitsOnUser`; a registry row is live when `remote.Current` is true and the row is present
  3. For a server row for `S`: a live daemon row with `SessionId == S` hides the server row (and marks the suppression time); a local row with `Status == "Completed"` and `_localConnected` is hidden by the server row; a local `Failed` row stays and hides the server row; a cached local row (`!_localConnected`) is hidden when the server row is ended and keeps hiding the server row when the server row is active; a retained registry row (`Current` false) is hidden by the server row
  4. Local-lane matching is gated on `LocalDaemonOnAppServer`; registry rows are never matched to server rows unless `appServerUrl` is set (the registry is the app's own server)
  5. Scope: under Mine, a daemon row with `SessionId == S` listed in `Live` with an owner id not in `ViewerIds` is hidden; a registry row whose session is not in `Live` is hidden under Mine; a local or pending row whose session is not listed stays; every daemon row whose session is in `Live` is published with `OwnerUserId`, `OwnerName` and `OwnerIsViewer` (`ViewerIds.Contains(owner)`) copied from that listing row, and every server row carries `OwnerIsViewer` the same way
  6. Server rows are keyed by the string the server returns; nothing normalises them

- [ ] **Step 1: Write the failing tests** (extend `Build` with a `FakeServerSessionsService` and return it)

```csharp
[Test] public async Task Server_rows_appear_beside_daemon_rows()
[Test] public async Task A_live_local_row_hides_the_server_row_for_its_session()
[Test] public async Task A_completed_local_row_yields_to_the_server_row()
[Test] public async Task A_failed_local_row_keeps_hiding_the_server_row()
[Test] public async Task A_cached_local_row_yields_to_an_ended_server_row_and_keeps_an_active_one_hidden()
  // local connected with a1(S) Running; then Status Unreachable (rows retained); server row S ended → server shows, local hidden; server row S active → local shows, server hidden
[Test] public async Task A_retained_registry_row_yields_to_the_server_row()
  // remote.CurrentSubject false, registry row b1(S) present, server row S ended → server shows, remote hidden; Current true → remote shows
[Test] public async Task A_server_row_keeps_hosted_presentation_within_the_grace()
  // FakeTimeProvider: local a1(S) live hides server S; remove a1; server S active → IsTerminalSession false; advance 31 s → true
[Test] public async Task Mine_hides_a_teammates_hosted_row_and_keeps_unknown_local_rows()
  // Scope Mine, ViewerIds {me}; registry b1(S1) with Live[S1].owner = other → hidden; local a2 with no session → shown; registry b2 with session not in Live → hidden; pending p1 → shown
[Test] public async Task Ownership_follows_the_live_listing_whatever_order_the_ids_arrive_in()
  // Scope Mine, ViewerIds empty: registry b1(S1) with Live[S1].owner "other" → hidden (the listing names another owner, ids not needed); local a2(S2) with Live[S2].owner "me" → shown with OwnerIsViewer null; ViewerIds {me} arrives → a2 shown with OwnerIsViewer true, b1 still hidden, b1's OwnerName "Tony Young" copied from the listing
[Test] public async Task Server_keys_are_not_normalised()
  // two rows "ABC" and "abc" → two keys
```

- [ ] **Step 2: Run to verify they fail**; **Step 3: Implement in `Recompute` and the subscriptions** (subscribe to `sessions.Live/MineLive/Ended` with `ToCollection`, `ScopeChanges`, `ViewerIds`, `remote.Current`; keep everything under `_lock` and one `_rows.Edit`)

- [ ] **Step 4: Run the whole `AgentDirectoryTests` class; expected all pass**

- [ ] **Step 5: Commit** — `git commit -m "Merge the server's sessions into the rail with liveness evidence (#1910)"`

---

### Task 7: Preferences remembered per user

**Files:**
- Create: `src/Capacitor.App/ViewModels/RailState.cs`, `RailGrouping.cs`, `RailSort.cs`, `RailPreferences.cs`, `src/Capacitor.App/Services/RailPreferencesStore.cs`
- Modify: `src/Capacitor.App/Services/AppStateStore.cs` (`AppState`)
- Test: `test/Capacitor.App.Tests.Unit/RailPreferencesStoreTests.cs`

**Interfaces:**
- Produces: `enum RailState { All, Live, Ended }`, `enum RailGrouping { None, Repository, Date, Status }`, `enum RailSort { LastUpdated, Created, Title }`
- `sealed record RailPreferences(RailScope Scope, RailState State, RailGrouping Grouping, RailSort Sort, bool Descending, IReadOnlyList<string> Collapsed)` with `static RailPreferences Default => new(RailScope.Mine, RailState.All, RailGrouping.Repository, RailSort.LastUpdated, true, [])`
- `AppState` gains `IReadOnlyDictionary<string, RailPreferences>? RailPreferencesByUser = null` and `IReadOnlyDictionary<string, string>? LastUserByProfile = null` (both before `UiScale`, the last member stays last)
- `RailPreferencesStore(IAppStateStore store)`: `Task<RailPreferences> LoadAsync(string serverUrl, string profileName)` returns the set of the profile's last user, else `Default`; `Task<RailPreferences> BindUserAsync(string serverUrl, string profileName, string userId)` records the profile's last user and returns that user's set (or `Default`); `Task SaveAsync(string serverUrl, string userId, RailPreferences prefs)`. Keys: `UserKey(serverUrl, userId) = $"{serverUrl.TrimEnd('/')}|{userId}"`, `ProfileKey(serverUrl, profileName) = $"{serverUrl.TrimEnd('/')}|{profileName}"`

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task Unknown_profile_loads_defaults()
[Test] public async Task Save_then_bind_returns_the_users_set_and_load_opens_on_the_last_user()
  // Save(url,"u1", Team/Ended/Date/Title/asc/["day-2026-10-23"]); BindUser(url,"p","u1"); fresh store: Load(url,"p") == that set
[Test] public async Task Two_users_on_one_profile_keep_separate_sets()
  // Save u1 set; BindUser(url,"p","u2") → Default; Load(url,"p") now opens on u2's (Default) set; Save u2 another set; Bind u1 again → u1's set
[Test] public async Task Preferences_round_trip_through_app_state_json()
  // AppStateStore write/read with RailPreferencesByUser populated equals the saved record
```

- [ ] **Step 2: Run to verify they fail**; **Step 3: Implement** (the dictionaries serialize through `AppStateJsonContext`; add `[JsonSerializable(typeof(RailPreferences))]` there); **Step 4: Run to verify they pass**

- [ ] **Step 5: Commit** — `git commit -m "Remember the rail's preferences per user (#1910)"`

---

### Task 8: The rail's controls, grouping, sorting and paging

**Files:**
- Rename: `src/Capacitor.App/ViewModels/RailRepoViewModel.cs` → `RailGroupViewModel.cs` (class `RailGroupViewModel`)
- Modify: `src/Capacitor.App/ViewModels/SessionRailViewModel.cs`, `src/Capacitor.App/Views/SessionRailView.axaml` (bindings `Rail.Groups`, `vm:RailGroupViewModel`), `src/Capacitor.App/Views/MainWindow.axaml` if it names `Repos`
- Test: `test/Capacitor.App.Tests.Unit/SessionRailViewModelTests.cs`, update `MainWindowViewModelTests.cs` references from `rail.Repos` to `rail.Groups`

**Interfaces:**
- Consumes: `RailPreferences` and the enums (Task 7), `IServerSessionsService` (Task 4), `AgentRow.IsEndedSession`/`LastActivityAt` (Task 5)
- Produces on `SessionRailViewModel`:
  - ctor appends `Action<string>? openServerSession = null, IServerSessionsService? sessions = null, RailPreferences? preferences = null, Action<RailPreferences>? persistPreferences = null`
  - `ReadOnlyObservableCollection<RailGroupViewModel> Groups` (replaces `Repos`)
  - settable `RailScope Scope`, `RailState State`, `RailGrouping Grouping`, `RailSort Sort`, `bool Descending`; each setter raises, applies, calls `persistPreferences(Preferences)`; `RailPreferences Preferences { get; }` (the current set, collapsed keys included); `void ApplyPreferences(RailPreferences prefs)` (used when the signed-in user resolves)
  - `ReactiveCommand<Unit, Unit> ShowMoreCommand` (`sessions.ShowMoreAsync`, can-execute from `Paging.CanShowMore`), `string LoadedText` (`"12 of 212"`, `""` when no server lane), `bool ShowsShowMore`, `bool AtCap` with `CapNote = "Search or narrow the scope for older sessions"`, `string? LiveNotShownText` (`"N more live sessions · narrow the scope"` or null)
  - `Scope` setter also sets `sessions.Scope`
- `RailGroupViewModel`: `string Key`, `string Label`, `RailGrouping Kind`, `bool ShowsHeader` (false for `None`), `bool IsNoRepository`, `RootDisplay`/`Tooltip` as before for Repository groups and `Label` for others; ctor takes `IGroup<AgentRow,string,string>`, the same observables as today, plus `IObservable<IComparer<RailSessionViewModel>> rowComparer` and `IObservable<RailGrouping> grouping`
- Grouping keys (static `SessionRailViewModel.GroupKey(AgentRow row, RailGrouping grouping, DateOnly today, TimeZoneInfo tz)`): Repository → `row.RepoGroupKey`; Date → `$"day:{localDay:yyyy-MM-dd}"` of `row.LastActivityAt ?? row.CreatedAt`; Status → `"status:0"` Waiting on you (`SessionStatusDots.NeedsAttention(row)` or `WaitsOnUser(row)`, not Failed), `"status:1"` Running (every other live row), `"status:2"` Failed (`row.Status == "Failed"`), `"status:3"` Ended (`row.IsEndedSession`); None → `"all"`
- Group labels: Date → "Today", "Yesterday", else `ddd d MMM` (invariant culture); Status → the four labels; None → ""; Repository → as today
- Group order (`GroupComparer`): Repository as today's `RepoComparer`; Date newest key first; Status by key; None single
- Row comparer (`RowComparer(RailSort sort, bool descending)`): LastUpdated on `row.LastActivityAt ?? row.CreatedAt`; Created on `CreatedAt`; Title `OrdinalIgnoreCase` on `Primary ?? Vendor`; direction flips the comparison; ties on `Id` then `Key` ascending
- State filter (`StateAdmits(RailState state, AgentRow row, string? selectedKey)`): All → true; Live → `!row.IsEndedSession`; Ended → `row.IsEndedSession`; a row whose key equals `selectedKey` is always admitted
- The pipeline: `directory.Rows.Connect().ObserveOn(Main).Filter(stateFilter /* observable predicate */).Group(GroupKeyOf, regrouper).Transform(...).DisposeMany().SortAndBind(_groupsSource, groupComparer /* observable */)`; `regrouper` fires on `Grouping` changes and on the minute tick when the local day changed; the selected key feeds the filter observable
- Collapsed groups: `_collapsedRepos` becomes `_collapsed` keyed by group key, seeded from `preferences.Collapsed`, persisted on toggle

- [ ] **Step 1: Write the failing tests** (`FakeAgentDirectory` rows via `AgentRow.FromLocal`/`FromServer`; `FakeServerSessionsService` for paging; `FakeTimeProvider` for the day roll)

```csharp
[Test] public async Task Default_grouping_is_by_repository_with_no_repository_last()
[Test] public async Task Date_grouping_names_today_and_yesterday_and_regroups_at_midnight()
  // rows with LastActivityAt today and yesterday; Grouping = Date → labels ["Today","Yesterday"]; advance the fake clock past local midnight and tick → yesterday's label becomes the weekday form
[Test] public async Task Status_grouping_orders_waiting_running_failed_ended_and_omits_empty_groups()
[Test] public async Task None_grouping_is_one_headerless_group()
[Test] public async Task Sort_by_title_ascending_orders_within_a_group_and_descending_flips_it()
[Test] public async Task Last_updated_sort_uses_last_activity_then_created()
[Test] public async Task Live_filter_hides_ended_rows_except_the_selected_one()
[Test] public async Task Ended_filter_hides_live_rows()
[Test] public async Task Show_more_forwards_to_the_service_and_the_footer_reads_loaded_of_total()
  // paging (12, 212, 1, true, false) → LoadedText "12 of 212", ShowsShowMore true; ShowMoreCommand → ShowMoreCalls == 1; paging AtCap → ShowsShowMore false, CapNote shown
[Test] public async Task Live_not_shown_renders_the_remainder_line()
[Test] public async Task Changing_a_control_persists_the_preferences_and_scope_reaches_the_service()
  // Scope = Team → persisted.Scope == Team and sessions.Scope == Team; Grouping = Date persisted; collapsing a group persists its key
[Test] public async Task Apply_preferences_rearranges_without_persisting()
```

- [ ] **Step 2: Run to verify they fail**; **Step 3: Implement**; **Step 4: Run `SessionRailViewModelTests` and `MainWindowViewModelTests`; expected all pass**

- [ ] **Step 5: Commit** — `git commit -m "Group, sort, filter and page the rail the user's way (#1910)"`

---

### Task 9: Server rows in the rail row

**Files:**
- Modify: `src/Capacitor.App/ViewModels/RailSessionViewModel.cs`, `src/Capacitor.App/Views/SessionRailView.axaml` (row template)
- Test: `test/Capacitor.App.Tests.Unit/RailSessionViewModelTests.cs`

**Interfaces:**
- Consumes: `AgentRow` server members (Task 5), `RailGrouping` observable from the group (Task 8)
- Produces on `RailSessionViewModel`: ctor appends `Action<string>? openServer = null, IObservable<RailGrouping>? grouping = null`; `bool IsServer`, `bool IsTerminalSession`, `bool IsEnded`, `string? OwnerInitials` (first letters of up to two words of `OwnerName`, upper-case), `bool ShowsOwner` (`OwnerName` present and `OwnerIsViewer != true`), `string RepoLabel` (`row.RepoGroupLabel`), `bool ShowsRepo` (grouping is not Repository and the row has a repository label other than "No repository"), `string OwnerTooltip` (`$"{OwnerName}\nOwner"`), `string TerminalTooltip = "Started in a terminal, not hosted by a daemon"`; `OpenCommand` calls `openServer(row.Id)` when `IsServer`; `IsStale` is `remoteStale || row.IsStale` for server rows; `Meta` for an ended row is `$"{RelativeTime.Format(EndedAt, now)} · {UptimeFormat.Format(EndedAt - CreatedAt)}"`, for a live server row the age as a local row, refreshed on the minute tick; the status presentation for an ended server row carries `Ended = true` (via `SessionStatusDots.ForRow` reading `row.IsEndedSession`)
- XAML: owner initials in a 16px circle (`KcapSurfaceRaisedBrush` fill, `KcapMutedBrush` text, `kcapTipCaption` tooltip) before the vendor mark when `ShowsOwner`; a `railChip` reading "terminal" with the `Terminal` Lucide icon when `IsTerminalSession`; the repository label as a `railMeta` text when `ShowsRepo`; the machine chip stays for remote rows

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task An_ended_server_row_shows_ended_meta_and_an_ended_status()
  // FromServer(ended, started 13:04, ended 16:56, now = ended + 2h) → Meta "2h ago · 3h 52m", Status.Ended true, IsEnded true
[Test] public async Task A_live_terminal_row_carries_the_chip_and_ages()
[Test] public async Task Owner_initials_show_for_another_users_row_only()
  // OwnerName "Tony Young", OwnerIsViewer false → "TY", ShowsOwner true; OwnerIsViewer true → ShowsOwner false
[Test] public async Task Repository_label_shows_only_when_not_grouped_by_repository()
[Test] public async Task Open_routes_a_server_row_to_the_server_opener()
[Test] public async Task A_stale_server_row_greys_out_like_a_stale_remote_one()
```

- [ ] **Step 2: Run to verify they fail**; **Step 3: Implement the view model, then the XAML**; **Step 4: Run `RailSessionViewModelTests`; expected all pass**

- [ ] **Step 5: Commit** — `git commit -m "Show server sessions as rail rows (#1910)"`

---

### Task 10: A session-only read-only workspace

**Files:**
- Modify: `src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs`, `src/Capacitor.App/Views/RemoteSessionView.axaml`
- Test: `test/Capacitor.App.Tests.Unit/RemoteSessionViewModelTests.cs`

**Interfaces:**
- Consumes: `AgentRow` server members (Task 5), `ReadOnlyChatInput`
- Produces on `RemoteSessionViewModel`: `bool IsSessionOnly => _row.Origin == AgentOrigin.Server`; `bool ShowsStop => !IsSessionOnly` (bound to `StopButton.IsVisible`); `Terminal` null when session-only whatever `surfaceFactory`; the chat input is `ReadOnlyChatInput` when session-only; `const string TerminalNote = "Running in a terminal · read-only here: answer it where it runs"`, shown as `AccessNote` while `Access == Ready`, `IsSessionOnly`, `!SessionEnded` and `_row.IsTerminalSession`; `RepoLabelText` for a session-only row is `row.RepoGroupLabel` plus `" · {OwnerName}"` when `OwnerName` is set
- `Apply(AgentRow row)` for a session-only row: `SessionEnded = row.IsEndedSession`; `PublishSession(ended: SessionEnded)`; never `Release()` on a terminal status; acquire the lease when `row.SessionId != _leasedSession`; never `_ended.OnNext`
- The directory subscription's `Remove` branch never flags `OriginChangedToLocal` for a session-only row

- [ ] **Step 1: Write the failing tests** (use `Harness`, with a `Row` overload building `AgentRow.FromServer`)

```csharp
[Test] public async Task A_session_only_ended_row_acquires_its_lease_and_shows_the_chat()
  // Build(FromServer(ended)); Access becomes Ready after the lane's watch and chat join; ShowsStop false; Terminal null; Chat input Availability ReadOnly; SessionEnded true; Ended never fires
[Test] public async Task A_live_terminal_row_shows_the_terminal_note_and_no_composer()
[Test] public async Task A_session_only_row_ending_keeps_the_lease_and_the_workspace()
  // row active → update row to ended: lease not released (no unchat call), SessionEnded true, Ended never fires
[Test] public async Task A_hosted_remote_row_keeps_todays_release_on_terminal_status()
```

- [ ] **Step 2: Run to verify they fail**; **Step 3: Implement**; **Step 4: Run the class; expected all pass**

- [ ] **Step 5: Commit** — `git commit -m "Open a server session read-only with a lease that follows it (#1910)"`

---

### Task 11: Opening server rows and the ended hand-off

**Files:**
- Modify: `src/Capacitor.App/ViewModels/MainWindowViewModel.cs`, `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs` (expose the session id and last status if not already public), `src/Capacitor.App/ViewModels/SessionRailViewModel.cs` (`NotifySessionOpened` for `Server`)
- Test: `test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs`, `test/Capacitor.App.Tests.Unit/WorkspaceNavigationTests.cs`

**Interfaces:**
- Consumes: `RemoteSessionViewModel.IsSessionOnly` (Task 10), `AgentDirectory.HandoverGrace` (Task 6)
- Produces: `MainWindowViewModel` ctor appends `Func<string, RemoteSessionViewModel?>? serverWorkspaceFactory = null`; `OpenSessionCore` routes `AgentOrigin.Server` to it; `SwapTo` sets `Rail.SelectedSessionKey = remote.IsSessionOnly ? $"server:{id}" : $"remote:{id}"`; `Superseded(open, lane)` treats a session-only workspace as superseded unless `lane == Server`; `WorkspaceViewModel` exposes `string? SessionId` and `string? LastStatus`
- `WatchOpen`: a local workspace's `Ended` with `LastStatus == "Completed"` and a known session id arms `EndedHandoff`; `Failed` or no session id closes as today. A hosted remote workspace's row removal (the directory `Remove` without an origin change) arms the same. `static IObservable<Unit> EndedHandoff(IAgentDirectory directory, string sessionId, TimeProvider time, TimeSpan grace)` completes with one value when `server:{sessionId}` exists in `directory.Rows` with `IsEndedSession` (or appears within `grace`), completes empty when the grace passes or the daemon row for that session returns live; on a value the window calls `Rebind(open, AgentOrigin.Server, false, false)`; on empty completion it calls `CloseWorkspace()` as today
- A session-only workspace needs no watch: `Ended` never fires for it

- [ ] **Step 1: Write the failing tests** (`MainWindowViewModelTests` style, with `host.New` for remote and a `FakeTimeProvider`)

```csharp
[Test] public async Task Opening_a_server_row_selects_its_server_key()
[Test] public async Task A_completed_local_workspace_hands_off_to_the_server_row_within_the_grace()
  // local a1 with session S opens; directory later holds server:S ended; a1 ends Completed → CurrentWorkspace is the server workspace for S, rail selection "server:S"
[Test] public async Task A_completed_local_workspace_closes_when_no_server_row_arrives()
  // advance 31 s → CurrentWorkspace null
[Test] public async Task A_failed_local_workspace_keeps_todays_behaviour()
[Test] public async Task A_removed_remote_row_hands_off_once_the_server_lists_the_session_ended()
[Test] public async Task A_remote_row_that_returns_within_the_grace_cancels_the_hand_off()
```

- [ ] **Step 2: Run to verify they fail**; **Step 3: Implement**; **Step 4: Run both classes; expected all pass**

- [ ] **Step 5: Commit** — `git commit -m "Hand a finished agent's workspace off to its session (#1910)"`

---

### Task 12: Composition, rail controls and the Pages rename

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs` (build `ServerSessionsService`, pass it to `AgentDirectory`, the rail, `originOf` with `server:` lookup, `BuildServer` factory, `RailPreferencesStore` load/bind/persist), `src/Capacitor.App/Views/SessionRailView.axaml` (control strip and footer), `src/Capacitor.App/Views/WorkspaceView.axaml` and `RemoteSessionView.axaml` (`Content="Pages"`)
- Test: `test/Capacitor.App.Tests.Unit/MainWindowSmokeTests.cs` (one interaction test), `test/Capacitor.App.Tests.Unit/AppStartupTests.cs` only if a wiring assertion exists for the rail

**Interfaces:**
- Consumes: everything above
- Composition: `var sessions = new ServerSessionsService(serverLane, ServerSessionsService.HttpFetch(sessionHttp, profiles), ServerSessionsService.HttpOwners(sessionHttp, profiles), _time, onUnauthorized: serverLane.ParkSignedOut)`; `new AgentDirectory(..., _time, sessions)`; `var prefsStore = new RailPreferencesStore(_appState)`; initial prefs `await prefsStore.LoadAsync(serverUrl, profileName)` (synchronously awaited at the same point window size is restored); `sessions.ViewerUserId.Where(id => id is not null).DistinctUntilChanged().ObserveOn(Main)` → `rail.ApplyPreferences(await prefsStore.BindUserAsync(...))`; `persistPreferences: prefs => prefsStore.SaveAsync(serverUrl, currentUserId ?? lastUser, prefs)`; `originOf` adds `directory.Rows.Lookup($"server:{id}").HasValue ? AgentOrigin.Server`; `RemoteSessionViewModel? BuildServer(string sessionId)` mirrors `BuildRemote` over the `server:` key with `surfaceFactory: null`
- XAML control strip, under the 44px chrome row and above New session: row 1 two `kcapSegment` borders with `kcapSegmentItem` buttons (Mine · Team; All · Live · Ended), `Classes.active` bound to the matching VM property; row 2 two `kcapChip` buttons opening `kcapPanel` flyouts with `kcapGhost` rows for Group (None, Repository, Date, Status) and Sort (Last updated, Created, Title), plus a `kcapGhost kcapIcon` toggle for direction (`ArrowDown`/`ArrowUp` Lucide icons); footer under the groups: `kcapGhost` button "Show 20 more" (`ShowMoreCommand`, `IsVisible=ShowsShowMore`), `railMeta` text `LoadedText`, and `CapNote`/`LiveNotShownText` texts when set; group header uses `Label` and hides when `!ShowsHeader`
- Pages: both tab buttons read `Content="Pages"`; `x:Name` and view-model names stay

- [ ] **Step 1: Write the failing smoke test** (`MainWindowSmokeTests` style, headless)

```csharp
[Test] public async Task Clicking_the_team_segment_changes_the_rails_scope_and_persists_it()
  // find the "Team" segment button by its automation name, click → Rail.Scope == Team and the persist callback received Team
```

- [ ] **Step 2: Run to verify it fails**; **Step 3: Implement the composition and the XAML**; **Step 4: Run the smoke class and build the app**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj` then the smoke filter. Expected: build clean, test passes

- [ ] **Step 5: Run the whole app suite**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: all pass (the `UiScaleTests` locale failure under nb-NO is pre-existing and unrelated; run it under `LANG=en_US.UTF-8` to confirm)

- [ ] **Step 6: Commit** — `git commit -m "Wire the server sessions lane into the app and rename the tab to Pages (#1910)"`

---

### Task 13: Slower PR reads for ended rows

**Files:**
- Modify: `src/Capacitor.App/Services/PullRequestToneCache.cs`
- Test: `test/Capacitor.App.Tests.Unit/PullRequestToneCacheTests.cs`

**Interfaces:**
- Produces: ctor appends `TimeSpan? endedRefreshEvery = null` (default 10 minutes); the row subscription also collects `endedSessions` (rows with `IsEndedSession`); an entry for a session in that set has `RefreshAfter = _endedRefreshEvery` after a settle instead of `_refreshEvery`

- [ ] **Step 1: Write the failing test**

```csharp
[Test] public async Task An_ended_sessions_pull_requests_refresh_on_the_slow_cadence()
  // FakeAgentDirectory with FromServer(ended) row S; settle one read; advance 2 min → no second read; advance 10 min → second read
```

- [ ] **Step 2: Run to verify it fails**; **Step 3: Implement**; **Step 4: Run the class; expected all pass**

- [ ] **Step 5: Commit** — `git commit -m "Read an ended session's pull requests less often (#1910)"`

---

### Task 14: Publish check and documentation

**Files:**
- Modify: `README.md` only if it describes the desktop rail (check `## Desktop app` or similar; the CLI surface is unchanged)

- [ ] **Step 1: AOT publish the CLI and check for trimming warnings** (the app shares `Capacitor.Remote.Models`)

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output

- [ ] **Step 2: Build the desktop app in Release**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj -c Release`
Expected: success

- [ ] **Step 3: Run the full solution test run as CI does**

Run: `dotnet test --solution Capacitor.slnx --max-parallel-test-modules 3 -- --maximum-parallel-tests 4`
Expected: green, or only the known locale failure

- [ ] **Step 4: Commit any README change** — `git commit -m "Describe the rail's session controls (#1910)"` (skip when nothing changed)
