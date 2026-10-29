# Desktop session search Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ⌘K opens a full-page session search over the server's `/api/search` in the desktop app, opening any hit in the app, and the rail's filters move behind a filter button

**Architecture:** A `SessionSearchViewModel` drives queries and results. `MainWindowViewModel` draws it as a cover over the main area without touching `CurrentWorkspace`. A hit the rail does not show is pinned through `ServerSessionsService`, which hydrates a `RepoSessionDto` from `api/sessions/{id}/summary` and publishes it on the snapshot. `AgentDirectory`'s merge takes the pin as an input, so resolution, suppression and ownership stay in one place

**Tech Stack:** .NET 10, Avalonia + ReactiveUI, DynamicData, System.Reactive, TUnit, WireMock.Net

**Spec:** `docs/superpowers/specs/2026-10-29-desktop-session-search-design.md`. Read it before any task

## Global Constraints

- Route: `GET {server}/api/search?q={query}&mode={sessions|transcripts}&limit=20`. Server JSON is snake_case, enums camelCase (`"active"`, `"ended"`)
- Debounce 150 ms after the last keystroke; minimum query length 3 after trimming; at most 20 rows; no paging; empty query shows nothing
- Matched spans arrive between U+0002 and U+0003 and are rendered bold in `KcapTextBrush`. The markers never reach the screen
- Tabs: Sessions and Transcripts only. Work Items is not shown
- Shortcut: ⌘K on macOS always; Ctrl+K elsewhere, yielding to a focused terminal as Ctrl+R does. The label comes from `ShortcutLabels.For("K", …)`
- Copy (exact):
  - "Search…"
  - "Type at least 3 characters"
  - "No matching sessions"
  - "No transcript matches"
  - "Search failed · try again"
  - "This session is no longer available"
  - "Couldn't open this session · try again"
  - "matched on branch" / "matched on PR title" / "matched on owner" / "matched on repository" / "matched by meaning"
  - "↑↓ move · ↵ open · tab switch · esc close"
- Filter dot when any of scope, state, grouping, sort or direction differs from `RailPreferences.Default`. Dot colour `KcapInfoBrush`. The filter panel starts collapsed on every launch and is not persisted
- Colours follow the CLAUDE.md desktop-app rules: no `KcapSuccess*`/`KcapWarning*` on titles, keys or chips. Controls use `kcapPanel`/`kcapGhost`/`kcapChip`/`kcapField`
- Icons are `AppIcon` (Lucide): `LucideIconKind.Search` for the magnifier, `LucideIconKind.ListFilter` for the filter button. No new geometry
- Comments follow CLAUDE.md's comment rules: scarce, no history, no spec coordinates. No `AI-<digits>` anywhere in `src/` or `test/` C#; run `scripts/check-linear-ids.sh` before pushing
- Tests use the existing fakes (`FakeServerLane`, `FakeServerSessionsService`, `FakeAgentDirectory`), `FakeTimeProvider`, and for HTTP `WireMockLane.BuildAsync` as in `ServerSessionsServiceHttpFetchTests`
- AOT: every new DTO goes in `RemoteModelsJsonContext`. After the last task, `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing, and the app project builds with no new warnings

## Review Focus

1. **A hit for a session the user is already looking at.** Expect the page to close and the same workspace to stay, with no rebuild or reconnect. The test is in Task 7
2. **Typing fast past 3 characters, then deleting back to 2.** Expect no request for the 2-character query, the in-flight one cancelled, and the "Type at least 3 characters" line rather than stale hits. The test is in Task 5
3. **A query that is all stopwords or punctuation** (`"--"`, `"the"`). The server answers `sessions: []`, `transcripts: null`. Expect the empty-results line for the active tab, not an error. The test is in Task 5
4. **Signed out or no server profile.** Expect ⌘K to still open the page, which shows the sign-in prompt and sends no request. The test is in Task 5
5. **The rail field clicked while the page is already open.** Expect the query text to be focused and selected, with no reset. The tests are in Tasks 7 and 9

---

### Task 1: Wire types for search and the session summary

**Files:**
- Create in `src/Capacitor.Remote.Models/`:
  - `SearchResponseDto.cs`
  - `SessionSearchHitDto.cs`
  - `TranscriptSearchHitDto.cs`
  - `ActivitySessionDto.cs`
  - `ActivitySessionRepositoryDto.cs`
- Modify:
  - `src/Capacitor.Remote.Models/RemoteWire.cs` (`ApiRoutes`)
  - `src/Capacitor.Remote.Models/RemoteModelsJsonContext.cs`
- Test: `test/Capacitor.Remote.Models.Tests.Unit/SearchWireTests.cs`

**Interfaces:**
- Produces:
  - `SearchResponseDto { SessionSearchHitDto[]? Sessions; TranscriptSearchHitDto[]? Transcripts; long TookMs }`
  - `SessionSearchHitDto`, one property per server field, snake_case names:
    - required: `session_id`, `slug`, `title`, `started_at` (string), `matched_field`, `snippet`
    - optional: `repo_owner`, `repo_name`, `pr_number` (int?), `pr_title`, `owner_user`, `owner_avatar_url`, `owner_user_id`, `owner_display_name`, `repo_hash`, `ended_at` (string?)
  - `TranscriptSearchHitDto`:
    - required: `session_id`, `snippet`
    - optional: `slug`, `title`, `event_index` (long), `speaker`, `timestamp` (string?), `owner_user_id`, `owner_user`, `owner_display_name`, `owner_avatar_url`, `repo_hash`, `repo_owner`, `repo_name`, `started_at` (string?), `ended_at` (string?), `agent_id`, `turn_index` (int?), `kind`
  - `ActivitySessionDto`:
    - required: `session_id`, `status` (string: "active"/"ended"), `started_at` (DateTimeOffset)
    - optional: `slug`, `title`, `vendor`, `cwd`, `ended_at`, `last_event_at`, `repo_branch`, `repo_owner`, `repo_name`, `owner_user_id`, `repositories` (`ActivitySessionRepositoryDto[]?`)
  - `ActivitySessionRepositoryDto { repo_hash (required), owner, repo_name, branch, is_primary }`
  - `ApiRoutes.Search(string query, string mode, int limit)` returns `api/search?q={escaped}&mode={mode}&limit={limit}`
  - `ApiRoutes.SessionSummary(string sessionId)` returns `api/sessions/{escaped}/summary`
  - `[JsonSerializable]` for `SearchResponseDto` and `ActivitySessionDto`

- [ ] **Step 1: Write the failing tests**:
  - `Search_response_reads_a_sessions_lane_and_leaves_transcripts_null`. A JSON body with one session hit carrying `\u0002local\u0003` in its snippet and `"transcripts":null` deserializes with `Sessions!.Length == 1`, `Transcripts is null`, and the snippet characters preserved
  - `Search_response_reads_a_transcript_hit_without_started_at_or_owner_id`
  - `Activity_session_reads_status_repositories_and_last_event`, with `"status":"ended"` and two repositories, one `is_primary: true`
  - `Search_route_escapes_the_query`: `ApiRoutes.Search("a b&c", "transcripts", 20) == "api/search?q=a%20b%26c&mode=transcripts&limit=20"`
- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet run --project test/Capacitor.Remote.Models.Tests.Unit -- --treenode-filter "/*/*/SearchWireTests/*"`. Expected: build errors for the missing types
- [ ] **Step 3: Add the DTOs, the two routes and the context entries.** One type per file. Use `[JsonPropertyName]` as `RepoSessionDto` does
- [ ] **Step 4: Run them and confirm they pass.** Same command. Expected: 4 passed
- [ ] **Step 5: Commit**: `Add wire types for session search and the session summary (#1948)`

### Task 2: HTTP reads for search and the summary

**Files:**
- Create: `src/Capacitor.App/Services/ServerSearchHttp.cs`
- Create (one type per file): `src/Capacitor.App/Services/SearchRequest.cs`, `SearchFetch.cs`, `SummaryFetch.cs`, `ServerReadOutcome.cs`
- Test: `test/Capacitor.App.Tests.Unit/ServerSearchHttpTests.cs`

**Interfaces:**
- Consumes: Task 1 types
- Produces:
  - `enum SearchMode { Sessions, Transcripts }`, in `SearchRequest.cs` with its wire-name extension `ToWire()` returning `"sessions"` or `"transcripts"`
  - `sealed record SearchRequest(string Query, SearchMode Mode, int Limit = 20)`
  - `enum ServerReadOutcome { Ok, Unauthorized, Unavailable, Failed }`. Unavailable means 403 or 404
  - `sealed record SearchFetch(ServerReadOutcome Outcome, SearchResponseDto? Response)`
  - `sealed record SummaryFetch(ServerReadOutcome Outcome, ActivitySessionDto? Session)`
  - `static class ServerSearchHttp`:
    - `Func<SearchRequest, CancellationToken, Task<SearchFetch>> Search(ICapacitorHttpClient? http, ProfileContext? profiles)`
    - `Func<string, CancellationToken, Task<SummaryFetch>> Summary(ICapacitorHttpClient? http, ProfileContext? profiles)`
  - Both follow `ServerSessionsService.HttpFetch`: use `ForWaitAsync`; when the auth status is not Ok or NoAuthRequired, or there is no server, return `Unauthorized`; a 401 response is `Unauthorized`; any other exception is `Failed`. `OperationCanceledException` propagates when the token is cancelled

- [ ] **Step 1: Write the failing tests** with WireMock:
  - `Search_sends_query_mode_and_limit_and_reads_the_lane`: the request has the `q`, `mode` and `limit` params, and the result is `Ok` with one hit
  - `Search_maps_401_to_unauthorized_and_500_to_failed`
  - `Summary_maps_404_and_403_to_unavailable`
  - `Search_propagates_cancellation`: the server delays 2 s and the token is cancelled at once, so the call throws `OperationCanceledException`
- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet run --project test/Capacitor.App.Tests.Unit -- --treenode-filter "/*/*/ServerSearchHttpTests/*"`
- [ ] **Step 3: Implement** the records, the enums and `ServerSearchHttp`
- [ ] **Step 4: Run them and confirm they pass**
- [ ] **Step 5: Commit**: `Read session search and session summaries from the server (#1948)`

### Task 3: Pinning in the sessions service

**Files:**
- Modify:
  - `src/Capacitor.App/Services/IServerSessionsService.cs`
  - `src/Capacitor.App/Services/ServerSessionsService.cs`
  - `src/Capacitor.App/Services/ServerSessionsSnapshot.cs`
- Create:
  - `src/Capacitor.App/Services/PinPresentation.cs`
  - `src/Capacitor.App/Services/PinOutcome.cs`
  - `src/Capacitor.App/Services/SessionIdentity.cs`
  - `src/Capacitor.App/Services/PinnedSessionRow.cs`, which maps the summary to a row
- Modify: `test/Capacitor.App.Tests.Unit/FakeServerSessionsService.cs`
- Test:
  - `test/Capacitor.App.Tests.Unit/ServerSessionsServicePinTests.cs`
  - `test/Capacitor.App.Tests.Unit/PinnedSessionRowTests.cs`

**Interfaces:**
- Consumes: `SummaryFetch`, `ActivitySessionDto` and `ServerReadOutcome` from Tasks 1 and 2
- Produces:
  - `sealed record PinPresentation(string? Username, string? DisplayName, string? AvatarUrl)`
  - `enum PinOutcome { Pinned, Unauthorized, Unavailable, Failed, Superseded }`
  - `sealed record SessionIdentity(int Account, int Generation, bool Connected)`. `Account` advances only on a subject change; `Generation` is the existing `_generation`
  - `ServerSessionsSnapshot(Live, MineLive, Ended, RepoSessionDto? Pinned = null)`. `Pinned` is non-null only while no listing supplies the pinned session
  - `IServerSessionsService` gains:
    - `string? PinnedSessionId { get; }`
    - `IObservable<SessionIdentity> Identity { get; }`, which replays the latest
    - `Task<PinOutcome> PinAsync(string sessionId, PinPresentation presentation, CancellationToken ct)`
    - `void Unpin()`
  - `static RepoSessionDto PinnedSessionRow.From(ActivitySessionDto summary, PinPresentation presentation)`:
    - `AccessLevel = ""`
    - `Status = summary.Status`
    - `LastActivityAt = LastEventAt ?? EndedAt ?? StartedAt`
    - `Repo` and `PrimaryRepoHash` come from the `is_primary` repository, else the first; `Branch = RepoBranch`
    - `Owner` is null when `OwnerUserId` is null, otherwise `UserId = OwnerUserId` with the presentation's username, display name and avatar
  - `ServerSessionsService` constructor gains `Func<string, CancellationToken, Task<SummaryFetch>>? fetchSummary = null`, inserted before `pushScheduler`

Behaviour (spec, "Pinning" and "Identity"):
- `PinAsync` when the session is in `Live`, `MineLive` or `Ended`: set the pin, issue no read, return `Pinned`
- `PinAsync` otherwise: read the summary. Publish only if all of these hold, otherwise return `Superseded`:
  - the pin lifetime is unchanged (no `Unpin` and no other `PinAsync` since)
  - the account is unchanged
  - no tombstone was set for the id after the read was issued
  - the token is not cancelled
- A `SessionAccessChanged` for the id during the read re-issues the read inside the same call. A `Deleted` push during it returns `Unavailable`
- Read failures: 401 returns `Unauthorized`, 403 or 404 return `Unavailable` and tombstone the id, anything else returns `Failed`. Nothing is pinned on any failure
- Refresh while the pin is held and no listing supplies it, on any of these:
  - `ActiveAdded`, `ActiveChanged`, `ActiveRemoved` or `TitleChanged` pushes for the id
  - `SessionAccessChanged` for the id
  - a reconnect
  - the cadence timer
- Refresh reads are numbered within a pin lifetime, and only the newest-issued one publishes. Every refresh is skipped while a listing supplies the id
- When the listing stops supplying a held pin, the last listed row becomes `Pinned` at once and a refresh is issued
- `Deleted` for the pinned id, or a refresh answering `Unavailable`, clears the pinned row (the pin id stays until `Unpin`) and tombstones the id
- `ClearForNewAccount` drops the pin and bumps `Account`. A `Scope` change does not touch the pin

- [ ] **Step 1: Write the failing `PinnedSessionRowTests`**:
  - `Maps_the_primary_repository_and_owner_presentation`
  - `Leaves_owner_null_without_an_owner_id_and_access_level_empty`
- [ ] **Step 2: Write the failing `ServerSessionsServicePinTests`**, using `Build()` from `ServerSessionsServiceTests` as the model, with a scripted summary fetch that can be held with a `TaskCompletionSource`:
  - `Pin_of_a_listed_session_reads_nothing_and_publishes_no_pinned_row`
  - `Pin_of_an_unlisted_session_publishes_the_summary_row_on_the_snapshot`
  - `Pin_survives_a_scope_change`
  - `Account_change_drops_the_pin_and_a_late_summary_publishes_nothing`: summary held, subject changes, the summary is released, `Pinned` stays null and the result is `Superseded`
  - `Deleted_push_during_the_initial_read_returns_unavailable`
  - `Access_change_during_the_initial_read_reissues_the_read`: two summary calls; the second answer is published
  - `A_newer_ended_refresh_is_not_overwritten_by_an_older_active_one`: two refreshes are held and released in reverse order, and the final `Pinned.Status == "ended"`
  - `Title_change_push_refreshes_the_pin`: the new title appears on `Pinned`
  - `Listing_row_wins_while_present_and_the_pin_rehydrates_after_it_leaves`: `Pinned` is null while listed, and becomes the last listed row the moment the listing drops it, followed by one summary call
  - `Unpin_then_late_summary_publishes_nothing`
  - `Identity_advances_account_on_subject_change_and_generation_on_reconnect`
- [ ] **Step 3: Run them and confirm they fail.** Run `dotnet run --project test/Capacitor.App.Tests.Unit -- --treenode-filter "/*/*/ServerSessionsServicePinTests/*"`, then the same with `PinnedSessionRowTests`
- [ ] **Step 4: Implement.** Pin state lives under `_lock` beside `_kept` and `_keptRow`. `EmitSnapshot` adds `Pinned` when the pin is held, unlisted and hydrated. The summary read runs outside the lock, and its result is re-checked under it, exactly as `RunAsync` checks `generation`. Update `FakeServerSessionsService` with:
  - settable `PinnedSubject` and `IdentitySubject`
  - a scriptable `PinResult`
  - recorded `PinCalls` and `UnpinCalls`
- [ ] **Step 5: Run them and confirm they pass.** Also run `ServerSessionsServiceTests` in full: no regressions
- [ ] **Step 6: Commit**: `Pin a searched session the listing does not supply (#1948)`

### Task 4: The pin in the directory merge, and session resolution

**Files:**
- Modify: `src/Capacitor.App/Services/AgentDirectory.cs` (`IAgentDirectory` is in this file)
- Modify: `test/Capacitor.App.Tests.Unit/FakeAgentDirectory.cs`
- Test: `test/Capacitor.App.Tests.Unit/AgentDirectoryPinTests.cs`

**Interfaces:**
- Consumes:
  - `ServerSessionsSnapshot.Pinned` and `IServerSessionsService.PinnedSessionId` from Task 3
  - the existing `SessionIds.Canonical` and `SessionIds.ServerToken`
- Produces: `IAgentDirectory.RowForSession(string sessionId)`, which returns `(string Id, AgentOrigin Origin)?`. It looks at the current published rows:
  - first the daemon rows whose session matches, compared as `MergeServerLane.ListedId` compares, and only those that `ServerSessionRows(…, _onAppServer.Value)` admits; local wins ties, as `SessionMap` decides
  - otherwise the `server:{sessionId}` row
  - otherwise null

Behaviour:
- In `MergeServerLane`, read the pinned session id from the snapshot subscription (store it with `_serverEnded`). Add `Pinned` to `listed` and to `byId` when it is not already there
- Under Mine, a daemon row whose listed id equals the pinned id is never removed by the scope filter
- For the pinned server row, ownership comes from `_viewerIds` under both scopes: `OwnerIsViewer = _viewerIds.Contains(owner)` when the ids are known, else null. It is never inferred from the Mine ended prefix

- [ ] **Step 1: Write the failing tests.** Use `AgentDirectoryTests` as the setup model:
  - `Pinned_teammate_session_shows_under_mine_with_the_teammates_owner`: `OwnerIsViewer == false` and the owner name is set
  - `Pinned_session_hosted_on_a_teammates_daemon_keeps_its_daemon_row_under_mine`: no server row is added beside it
  - `Row_for_session_prefers_the_local_row_of_an_unproven_twin`
  - `Row_for_session_returns_a_failed_local_row_over_the_server_row`
  - `Row_for_session_matches_a_dashed_daemon_session_id_to_the_server_spelling`
  - `Row_for_session_ignores_local_rows_while_the_local_daemon_is_on_another_server`
  - `Row_for_session_returns_the_server_row_for_a_session_only_session`
- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet run --project test/Capacitor.App.Tests.Unit -- --treenode-filter "/*/*/AgentDirectoryPinTests/*"`
- [ ] **Step 3: Implement** in `AgentDirectory`. Add `RowForSession` to `FakeAgentDirectory` as a settable dictionary
- [ ] **Step 4: Run them and confirm they pass.** Also run `AgentDirectoryTests` in full
- [ ] **Step 5: Commit**: `Merge the pinned session into the directory and resolve hits by session (#1948)`

### Task 5: Search page view model, queries and results

**Files:**
- Create:
  - `src/Capacitor.App/ViewModels/SessionSearchViewModel.cs`
  - `src/Capacitor.App/ViewModels/SearchHitViewModel.cs`
  - `src/Capacitor.App/ViewModels/SearchSnippet.cs`
  - `src/Capacitor.App/ViewModels/SnippetRun.cs`
  - `src/Capacitor.App/ViewModels/SearchListState.cs`
- Test:
  - `test/Capacitor.App.Tests.Unit/SessionSearchViewModelTests.cs`
  - `test/Capacitor.App.Tests.Unit/SearchSnippetTests.cs`

**Interfaces:**
- Consumes:
  - `SearchRequest`, `SearchMode`, `SearchFetch` and `ServerReadOutcome` from Task 2
  - `SessionIdentity` from Task 3
- Produces:
  - `sealed record SnippetRun(string Text, bool Matched)`
  - `static IReadOnlyList<SnippetRun> SearchSnippet.Parse(string? snippet)`. It splits on U+0002/U+0003, drops the markers, treats an unterminated U+0002 as running to the end, and merges adjacent runs with the same `Matched`
  - `enum SearchListState { Empty, TooShort, Searching, Results, NoResults, Failed, SignInRequired }`
  - `SearchHitViewModel : ReactiveObject`, with:
    - `SessionId`
    - `SearchMode Kind`
    - `IReadOnlyList<SnippetRun> Title`
    - `string? Speaker`
    - `IReadOnlyList<SnippetRun> Snippet`
    - `string Caption`
    - `int? PrNumber`
    - `string? OwnerInitials`
    - `string? OwnerAvatarUrl`
    - `string WhenText`
    - `PinPresentation Presentation`
    - `bool IsHighlighted`
    - `bool IsOpening`
  - `SessionSearchViewModel : ReactiveObject, IDisposable`. Constructor:
    - `Func<SearchRequest, CancellationToken, Task<SearchFetch>> search`
    - `IObservable<SessionIdentity> identity`
    - `IObservable<string> accessChanged`
    - `TimeProvider time`
    - `IScheduler scheduler`
    - `TimeSpan? debounce = null`, defaulting to 150 ms
  - Its members:
    - `string Query { get; set; }`
    - `SearchMode Tab { get; set; }`
    - `ReadOnlyObservableCollection<SearchHitViewModel> Hits`
    - `int HighlightIndex`
    - `SearchListState ListState`
    - `string? ListMessage`, the exact copy from Global Constraints
    - `bool IsLoading`
    - `void MoveHighlight(int delta)`, clamped at both ends
    - `void NextTab()` and `void PreviousTab()`
    - `void Reset()`, which clears the query and hits, sets the tab to Sessions and cancels in-flight work

Behaviour (spec, "Query and requests", "Results", "Keyboard" and "Identity"):
- Query changes are debounced on `scheduler`. A tab change runs at once
- A trimmed query of length 1–2 sends nothing and shows `TooShort`; an empty query shows `Empty`
- Each request cancels the previous one. A response applies only when its (query, tab, `Account`, `Generation`) match the current values
- An `accessChanged` element cancels the in-flight request and re-runs the current query
- A new `Account` resets everything. A new `Generation` with `Connected` re-runs the current non-empty query
- Results:
  - `Unauthorized` shows `SignInRequired`
  - `Failed` shows `Failed`
  - `Ok` with a null or empty lane for the active tab shows `NoResults`
- Captions:
  - the session caption is `owner/repo · owner name`, plus a matched-on hint for `branch`, `pr`, `user`, `repo` or `semantic`
  - the transcript speaker is shown as `user`, `assistant` or `summary`; `summary` is used when `kind == "turn_prose"`
- `WhenText`:
  - a session hit shows `RelativeTime.Format(startedAt, now)`, or "Live" when `endedAt` is null
  - a transcript hit shows `RelativeTime.Format(timestamp ?? startedAt, now)`, or empty when both are missing
- Owner initials come from `RailSessionViewModel.Initials`; make it `internal static` if it is private
- The highlight resets to 0 on every new result set

- [ ] **Step 1: Write the failing `SearchSnippetTests`**:
  - `Parses_marked_spans_and_drops_the_markers`: `"a\u0002loc\u0003al"` gives `[("a",false),("loc",true),("al",false)]`
  - `Unterminated_marker_runs_to_the_end`
  - `Null_and_plain_text_give_one_unmatched_run`
- [ ] **Step 2: Write the failing `SessionSearchViewModelTests`** with a `TestScheduler`, a scripted search function that records requests and can hold each one, and subjects for identity and access:
  - `Waits_150ms_after_the_last_keystroke`: "loc", then "loca" 100 ms later, gives exactly one request, for "loca", at 250 ms
  - `Short_query_sends_nothing_and_cancels_the_in_flight_request`: "local", then "lo", gives `ListState == TooShort` and the first request's token cancelled
  - `Empty_query_shows_nothing`
  - `Tab_switch_reruns_immediately_for_the_new_mode`
  - `Stale_response_for_an_older_query_is_dropped`
  - `Stopword_query_answer_shows_no_results`: the response has `sessions: []` and `transcripts: null` while Transcripts is active, giving `NoResults` and the message "No transcript matches"
  - `Unauthorized_shows_the_sign_in_state`
  - `Failure_shows_the_error_line_and_keeps_no_hits`
  - `Account_change_clears_and_drops_the_late_response`
  - `Reconnect_reruns_the_current_query`
  - `Access_change_reruns_and_drops_the_pre_change_response`
  - `Highlight_moves_and_clamps_and_resets_on_new_results`
  - `Session_caption_names_the_matched_field`: `semantic` gives "matched by meaning", `title` gives no hint
- [ ] **Step 3: Run them and confirm they fail.** Run `dotnet run --project test/Capacitor.App.Tests.Unit -- --treenode-filter "/*/*/SessionSearchViewModelTests/*"`
- [ ] **Step 4: Implement** the types above. Use one `SerialDisposable` or `CancellationTokenSource` swap for the in-flight request, and marshal results with `ObserveOn(RxSchedulers.MainThreadScheduler)` in production; the scheduler parameter is that seam
- [ ] **Step 5: Run them and confirm they pass**
- [ ] **Step 6: Commit**: `Search sessions and transcripts from a page view model (#1948)`

### Task 6: Opening a hit

**Files:**
- Modify: `src/Capacitor.App/ViewModels/SessionSearchViewModel.cs`
- Create: `src/Capacitor.App/ViewModels/SearchOpenOutcome.cs`
- Test: `test/Capacitor.App.Tests.Unit/SessionSearchOpenTests.cs`

**Interfaces:**
- Consumes: `PinPresentation` from Task 3, and `SearchHitViewModel` from Task 5
- Produces:
  - `enum SearchOpenOutcome { Opened, SignInRequired, Unavailable, Failed, Superseded }`
  - a constructor parameter `Func<string, PinPresentation, CancellationToken, Task<SearchOpenOutcome>> open`, inserted before `debounce`
  - `Task OpenAsync(SearchHitViewModel hit)`, which is also what Enter does on the highlighted row
  - `void CancelIntent()`
  - `IObservable<Unit> Closed`

Behaviour (spec, "The open operation"):
- A new `OpenAsync` cancels the previous intent's token. So do `CancelIntent`, `Reset` and an `Account` change
- While the intent runs, its hit's `IsOpening` is true
- Results:
  - `Opened` raises `Closed`
  - `Superseded` changes nothing
  - `SignInRequired` sets `ListState = SignInRequired`
  - `Unavailable` and `Failed` set `ListMessage` to the exact copy, keep the hits and keep the page open

- [ ] **Step 1: Write the failing tests**:
  - `Second_hit_cancels_the_first_intent`
  - `Reset_cancels_a_pending_open`
  - `Account_change_cancels_a_pending_open`
  - `Unavailable_keeps_the_page_open_with_the_message`
  - `Opened_raises_closed`
  - `Enter_opens_the_highlighted_hit`
- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet run --project test/Capacitor.App.Tests.Unit -- --treenode-filter "/*/*/SessionSearchOpenTests/*"`
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run them and confirm they pass**, along with `SessionSearchViewModelTests`
- [ ] **Step 5: Commit**: `Open a search hit through a cancellable intent (#1948)`

### Task 7: The main window's cover, navigation and pin release

**Files:**
- Modify: `src/Capacitor.App/ViewModels/MainWindowViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/MainWindowSearchTests.cs`

**Interfaces:**
- Consumes:
  - `IAgentDirectory.RowForSession` from Task 4
  - `IServerSessionsService.PinAsync`, `Unpin` and `PinnedSessionId` from Task 3
  - `SessionSearchViewModel` and `SearchOpenOutcome` from Tasks 5 and 6
- Produces on `MainWindowViewModel`:
  - `SessionSearchViewModel? Search { get; }`, null without a server sessions service
  - `bool IsSearchOpen`
  - `ReactiveCommand<Unit, Unit> OpenSearchCommand`
  - `ReactiveCommand<Unit, Unit> DismissSearchCommand`
  - `ReactiveCommand<Unit, Unit> NewSessionCommand`
  - `IObservable<Unit> SearchFocusRequests`
  - `Task<SearchOpenOutcome> OpenSearchHitAsync(string sessionId, PinPresentation presentation, CancellationToken ct)`
  - a constructor parameter `Func<IObservable<SessionIdentity>, SessionSearchViewModel>? searchFactory = null`, inserted before `sessions`
- Changes to existing code:
  - The `Meta+N`/`Ctrl+N` bindings and the New session button bind to `NewSessionCommand` (Task 9 rebinds the XAML). `CloseWorkspaceCommand` stays for the coordinator

Behaviour (spec, "Hosting and navigation" and "Opening a hit"):
- `OpenSearchCommand`:
  - when closed: `Search.Reset()`, `IsSearchOpen = true` and `_navigation.Bump()`
  - when already open: emit on `SearchFocusRequests` only
- `DismissSearchCommand` (Escape): `IsSearchOpen = false` and `Search.CancelIntent()`. `CurrentWorkspace` is untouched
- User navigation means the public `OpenSession(…)`, `NewSessionCommand` and `OpenSearchHitAsync` succeeding. It sets `IsSearchOpen = false` and cancels the intent, and it calls `_sessions?.Unpin()` unless the session it opens is the pinned one
- Automatic paths (`CloseWorkspace` from `WatchOpen` and the hand-off, and the `OpenSessionCore` calls inside `Rebind`) change neither `IsSearchOpen` nor the pin
- `OpenSearchHitAsync`:
  1. If `RowForSession` already answers, call `OpenSession(id, origin)`
  2. Otherwise call `PinAsync`; on `Pinned` with the token not cancelled and `IsSearchOpen` still set, resolve again with `RowForSession` and open
  3. Map the `PinOutcome` failures to `SearchOpenOutcome`
  4. Opening the session that is already open returns `Opened`, closes the page and rebuilds nothing

- [ ] **Step 1: Write the failing tests.** Use `MainWindowViewModelTests` construction as the model, with `FakeAgentDirectory` and `FakeServerSessionsService`:
  - `Opening_search_keeps_the_current_workspace_and_bumps_navigation`
  - `Escape_uncovers_the_workspace_without_teardown`
  - `Covered_workspace_hand_off_does_not_dismiss_search`: the workspace's session ends and is rebound while `IsSearchOpen` stays true
  - `Delayed_pin_still_opens_after_a_covered_hand_off`: the `PinAsync` result is held, the covered workspace is rebound, then the pin is released and the hit opens
  - `Delayed_pin_after_escape_opens_nothing`
  - `Rail_click_dismisses_search_and_releases_the_pin`
  - `New_session_dismisses_search`
  - `Opening_the_already_open_session_closes_search_without_rebuild` (Review Focus 1): the same `CurrentWorkspace` instance, and no teardown tracked
  - `Shortcut_while_open_requests_focus_without_reset` (Review Focus 5)
  - `Opening_search_invalidates_a_pending_launch_auto_open`: capture `NavigationGeneration`, open search, then `OpenSessionIfCurrent` with the old generation opens nothing
- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet run --project test/Capacitor.App.Tests.Unit -- --treenode-filter "/*/*/MainWindowSearchTests/*"`
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run them and confirm they pass**, along with `MainWindowViewModelTests` in full
- [ ] **Step 5: Commit**: `Cover the main area with search and dismiss it on user navigation (#1948)`

### Task 8: Rail filter toggle and dot

**Files:**
- Modify: `src/Capacitor.App/ViewModels/SessionRailViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/SessionRailFilterTests.cs`

**Interfaces:**
- Produces on `SessionRailViewModel`:
  - `bool FiltersOpen`, false at construction
  - `ReactiveCommand<Unit, Unit> ToggleFiltersCommand`
  - `bool FiltersDiffer`, true when (Scope, State, Grouping, Sort, Descending) differs from `RailPreferences.Default`; it is raised on every change of those five

- [ ] **Step 1: Write the failing tests**:
  - `Filters_start_closed_and_toggle`
  - `Dot_shows_when_any_control_differs_from_the_defaults`: one case per control, and a return to the defaults clears it
  - `Locked_scope_with_defaults_shows_no_dot`
- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet run --project test/Capacitor.App.Tests.Unit -- --treenode-filter "/*/*/SessionRailFilterTests/*"`
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run them and confirm they pass**
- [ ] **Step 5: Commit**: `Track whether the rail's hidden filters differ from the defaults (#1948)`

### Task 9: Views, shortcut, menu and composition

**Files:**
- Create:
  - `src/Capacitor.App/Views/SessionSearchView.axaml`
  - `src/Capacitor.App/Views/SessionSearchView.axaml.cs`
  - `src/Capacitor.App/Views/SearchSnippetText.cs`, an attached property or small control that renders `IReadOnlyList<SnippetRun>` as `Run`s in a `TextBlock`, with matched runs SemiBold+ in `KcapTextBrush` and the rest in the row's brush
- Modify:
  - `src/Capacitor.App/Views/SessionRailView.axaml` and `.axaml.cs`
  - `src/Capacitor.App/Views/MainWindow.axaml` and `.axaml.cs`
  - `src/Capacitor.App/Views/AppMenuBar.cs`
  - `src/Capacitor.App/Views/ShortcutLabels.cs`, adding `Search { get; } = For("K", UsesCommandKey)`
  - `src/Capacitor.App/App.axaml.cs`, which wires `ServerSearchHttp.Search`, `ServerSearchHttp.Summary`, the `searchFactory` and `lane.SessionAccessChanged`
- Test:
  - `test/Capacitor.App.Tests.Unit/SessionSearchViewSmokeTests.cs`
  - `MainWindowSmokeTests.cs`: add cases
- Docs: check `docs/` for the desktop figures harness (from #1989) and re-render any figure showing the rail top

Layout (spec, "The rail top" and "The search page"; mockup https://kurrent.kcap.ai/pages/b0efa91e143d4c609f6ff62377a92474):
- Rail, below New session: a `Grid ColumnDefinitions="*,Auto" ColumnSpacing="4" Margin="10,0,10,8"`:
  - The field: a `Button` styled as `kcapField`, `x:Name="RailSearchButton"`, `Command=OpenSearchCommand`, `AutomationProperties.Name="Search sessions"`. On the left it shows `Search… {ShortcutLabels.Search}`, or `Search.Query` while open. On the right it has `AppIcon Kind=Search`
  - The filter button: `kcapGhost kcapIcon`, `x:Name="RailFilterButton"`, `AppIcon Kind=ListFilter`, with `Classes.active` bound to `FiltersOpen` and the dot as a 6px `Ellipse` in `KcapInfoBrush` bound to `FiltersDiffer`
  - Today's `RailControls` panel stays as it is, with `IsVisible` bound to `FiltersOpen` added
- Window:
  - `SessionSearchView`, `x:Name="SearchCover"`, sits in `ContentPane` above `LauncherPane` and `WorkspaceHost`, with `IsVisible=IsSearchOpen`. While it is visible, those two are hidden by binding (not removed), so the workspace stays alive
  - Key bindings: Meta+K, plus Ctrl+K off macOS through the `RefreshUnlessTerminalFocused` pattern, bound to `OpenSearchCommand`; Meta+N and Ctrl+N rebound to `NewSessionCommand`
- Page:
  - A 52px query row with `AppIcon Search`, a `TextBox` with the `kcapEmbedded` class bound to `Search.Query`, and an "esc" chip
  - The tab chips Sessions and Transcripts, styled `kcapChip` with `Classes.active`
  - A progress line bound to `IsLoading`
  - The hit list (`ItemsControl` of `SearchHitViewModel`: avatar, title runs, `#N` chip, caption, when; transcript rows put the snippet runs below)
  - A `ListMessage` line, and the footer copy
  - Code-behind:
    - Focus the `TextBox` when the cover is shown and on `SearchFocusRequests`, and select its text on the latter
    - The `TextBox` handles these keys in a tunnel `KeyDown`: Up/Down call `MoveHighlight`, Enter opens, Tab/Shift+Tab switch tabs, Escape runs `DismissSearchCommand`
    - Hovering a row sets the highlight
  - Sign-in state: reuse the sign-in prompt the rail footer uses (`SignInCommand`)
- Menu: in `AppMenuBar`, an item "Search Sessions" with `new KeyGesture(Key.K, KeyModifiers.Meta)`, placed beside Refresh, as `RefreshItem` is built

- [ ] **Step 1: Write the failing smoke tests**, in the headless style of `MainWindowSmokeTests`:
  - `Rail_shows_search_field_and_hides_filters_until_toggled`
  - `Filter_dot_follows_filters_differ`
  - `Meta_k_opens_the_cover_and_focuses_the_query`
  - `Escape_in_the_query_hides_the_cover_and_keeps_the_workspace_view`
  - `Rail_field_click_while_open_selects_the_query_text` (Review Focus 5)
  - `Ctrl_k_yields_to_a_focused_terminal_off_mac`, following the existing Ctrl+R test
  - `Matched_snippet_runs_render_bold_without_markers`: the rendered `TextBlock` text contains no U+0002 or U+0003
  - `Signed_out_page_shows_sign_in_and_sends_no_request` (Review Focus 4)
- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet run --project test/Capacitor.App.Tests.Unit -- --treenode-filter "/*/*/SessionSearchViewSmokeTests/*"`, then the same with `MainWindowSmokeTests`
- [ ] **Step 3: Implement** the views, bindings, menu item and `App.axaml.cs` composition
- [ ] **Step 4: Run the full app suite.** Run `dotnet run --project test/Capacitor.App.Tests.Unit`. Expected: green, apart from the known flakes in memory (nudge/lease under CPU contention; rerun alone before calling a failure a regression)
- [ ] **Step 5: Verify the build and AOT.** Run `dotnet build src/Capacitor.App/Capacitor.App.csproj` and expect no new warnings. Then run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` and expect no output. Run `scripts/check-linear-ids.sh` and expect it clean
- [ ] **Step 6: Try it in the running app** (skill `run`): open with ⌘K, type a query, switch tabs, open a teammate's ended session under Mine, Escape back, and check the filter toggle and dot
- [ ] **Step 7: Commit**: `Show the session search page and the rail's search field (#1948)`
