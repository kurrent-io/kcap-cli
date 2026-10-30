# Desktop session search

GitHub: [#1948](https://github.com/kurrent-io/kcap-cli/issues/1948). Agreed with the owner on 2026-10-29 against the mockup [Desktop session search mockup](https://kurrent.kcap.ai/pages/b0efa91e143d4c609f6ff62377a92474) (private kcap page, version 1), with one change: an empty query shows nothing

This design replaces D6 of [the past-sessions design](2026-10-24-desktop-past-sessions-design.md#d6--k-searches-the-server). That one listed hits inline under a rail field, queried `api/sessions/search` and filtered to the Mine scope in the app. None of it was built

## Intent

Finding a session in the desktop app today means scrolling the rail, which holds the live sessions and a capped prefix of ended ones. The web app has a ⌘K palette that searches every session the user can see. The desktop app should offer the same search, behave the way web does, and open whatever it finds inside the app, so the user never has to switch to the browser to find a session

## Goals

- The rail's top gets a search field and a filter button; the existing filter and sort controls hide behind the filter button
- ⌘K (Ctrl+K off macOS) or a click on the field opens a full-size search page in the main area, its own query box focused
- The page searches **Sessions** and **Transcripts**, switchable, with the same ranking as the web palette
- Selecting a hit opens that session in the app, read-only when it has ended or the app has no daemon for it, whether or not the rail has it loaded

## Non-goals

- **Work items.** The web palette has a third tab; the desktop app has no work-item page, so the tab is postponed and not shown
- Jumping to the matched moment of a transcript hit. Web opens the session at the top; so does this (builder's note: a later change, not this one)
- Recent sessions on an empty query. Web shows the last ten opened; the owner chose Linear's behaviour instead: nothing
- Paging past the first 20 hits. Web has no paging either
- Any kcap-server change. The route below exists and is what the web palette's ranking runs on

## Anti-goals

- Typing in the rail. The rail field is a launcher for the page, not an input; text never enters it
- Hammering the server. One request per pause in typing, never one per keystroke, and an in-flight request is cancelled when the next one starts
- A search hit that opens nothing, or opens the web app. Every session hit opens in the app

## The rail top

From the top: the 44 px chrome strip, **New session ⌘N** (unchanged), then one new row:

- A field-styled button filling the width: placeholder "Search…" on the left with the shortcut label (`ShortcutLabels.For("K", …)`, so "⌘K" on macOS, "Ctrl+K" elsewhere), a magnifier glyph on its right edge. Clicking anywhere on it opens the search page
- A filter icon button (`kcapGhost kcapIcon`) to its right. It toggles a panel under the row holding today's two control rows unchanged: Mine/Team with All/Live/Ended, then Group, Sort and the direction arrow
- While any of scope, state, grouping, sort or direction differs from the defaults (Mine · All · Repository · Last updated · descending, as the past-sessions design fixes them), the filter icon carries a small dot, so hidden filters are never silently narrowing the rail. The dot uses `KcapInfo*` (selection / state, not status)

The panel starts collapsed on each launch (my call: persisting it is cheap, but the point of the change is a quieter rail). The magnifier and filter glyphs are the existing Lucide kinds `LucideIconKind.Search` and `LucideIconKind.ListFilter`, drawn through `AppIcon`; no new geometry

## The search page

### Hosting and navigation

The page is a cover over the main area, not a navigation target. `MainWindowViewModel` gains a search-open state; while it is set the page is drawn over the launcher or the workspace and hides them, and `CurrentWorkspace` stays exactly what it was. The covered workspace keeps every watch it has today: if it completes, hands off to a server workspace, or closes while the page is up, those transitions run as usual and the page simply covers their result. Escape clears the state and uncovers whatever is current by then, so there is no saved return reference to go stale

Opening the page advances `NavigationGate`, so a launch's pending auto-open (`OpenSessionIfCurrent`) yields to it as it yields to any other navigation. Any user navigation dismisses the page: a rail click, New session (⌘N), opening a hit, and opening a hit for the session that is already open, where `OpenSessionCore` returns early without `SwapTo` and the page must still close. Automatic transitions of the covered workspace (completion, hand-off, deletion) do not

While the page is showing, the rail's search field shows the current query in place of the placeholder (as in the mockup), and the rail keeps its selection

Each opening starts fresh: empty query, Sessions tab (my call; Linear does the same)

### Shortcut

⌘K on macOS opens the page from anywhere in the main window, including while the terminal has focus. Off macOS, Ctrl+K yields to a focused terminal, as Ctrl+R already does (`RefreshUnlessTerminalFocused`), because Ctrl+K is a terminal key there. Pressing the shortcut while the page is open focuses and selects the query text. It is mirrored as a native menu item with the gesture, next to the existing ones in `AppMenuBar`

### Query and requests

- `GET {server}/api/search?q={query}&mode={sessions|transcripts}&limit=20`, through the app's authenticated `ICapacitorHttpClient`, bearer token as every other read. `mode` omitted means sessions; the builder may send it explicitly
- A request goes out 150 ms after the last keystroke, matching the web palette. A query shorter than 3 characters after trimming sends nothing and shows "Type at least 3 characters"
- Each new request cancels the previous one, and a response is discarded on arrival unless it matches the current (query, tab, identity generation): see Identity below
- A `SessionAccessChanged` push for any session cancels the in-flight search and re-runs the current query, and a response issued before the push is dropped, so a hit for a session whose access was just narrowed is never shown from a pre-change read. The rows already on screen are replaced by the re-run's
- Switching tab re-runs the current query for the new tab at once (no debounce)
- An empty query shows an empty page: no list, no recents
- A thin progress line under the query box while a request is in flight
- The response carries only the requested lane (`sessions` or `transcripts`); the other is null. An unusable query returns `sessions: []` and `transcripts: null` whatever the mode, so the client treats a null lane as no hits

### Results

Up to 20 rows. Matched words arrive wrapped in U+0002 … U+0003; the row renders those spans bold and in `KcapTextBrush`, and the markers never reach the screen. Semantic-only session hits carry no markers and render plain

**Sessions row:** owner initials avatar (as the rail draws owners), the snippet as title (the highlighted title, or the slug if untitled), a `#N` chip when `prNumber` is set, a caption `owner/repo · owner name`, and on the right when it started, or "Live" when `endedAt` is null. When `matchedField` is `branch`, `pr`, `user` or `repo` the caption adds "matched on branch / PR title / owner / repository"; `semantic` adds "matched by meaning"; `title` adds nothing

**Transcripts row:** avatar, the session title with the speaker after it in muted text (user, assistant, or "summary" for a turn-prose hit), the snippet below, and when on the right. Several rows may name the same session; they are not collapsed

Empty results: "No matching sessions" / "No transcript matches". A failed request shows a one-line error in the list area with the status ("Search failed · try again"), not a silent clear. With no server profile, or signed out, the page shows the sign-in prompt the app uses elsewhere instead of the list; the rail field still opens the page

### Keyboard

- Up/Down move the highlight, clamped at both ends; mouse hover moves it too. The highlight resets to the first row on each new result set
- Enter opens the highlighted row; click opens a row
- Tab switches Sessions ⇄ Transcripts; Shift+Tab goes back. Focus stays in the query box throughout
- Escape closes the page and returns to what was showing before
- A footer line lists these: "↑↓ move · ↵ open · tab switch · esc close"

## Opening a hit

A hit opens exactly what a rail click on that session would open, under whatever scope and state the rail is in. Hits name a session id; a transcript hit's agent id names a subagent inside it and is not used for navigation

### Resolution

The session id resolves to the row the directory publishes for that session after its own merge: `MergeServerLane`'s daemon/server suppression, with its session-id normalization and the local-server identity guard (`LocalDaemonOnAppServer`), decides which rows survive, and when more than one daemon row survives for the session (a local and a remote twin whose identity is unproven, which the merge deliberately keeps both of), the existing `SessionMap` tie-breaker picks one: local first. Search adds no rule of its own. That keeps the past-sessions rules without restating them: a live hosted agent on this machine opens its local workspace, one on another of the user's daemons opens the remote host, a Failed or Interrupted local row opens its local workspace with its reason (it suppresses the server row), and anything else opens the read-only workspace. `DaemonRowFor` is not the rule: it requires `IsHostedNow` and rejects Failed rows. Calling `OpenSession(sessionId, AgentOrigin.Server)` directly is not enough either, because daemon rows are keyed by agent id and an explicit lane skips resolution

The read-only workspace opens on the tab a rail click opens. The past-sessions design's Overview tab is not built yet; until it is, that is Chat, and when it lands search follows it with no change here

### Pinning

Every session opened from search is **pinned** (owner's decision) for as long as it stays selected, whether or not the rail had a row for it when it was chosen. The pin is keyed by session id, so it survives the session's own lane hand-offs (local to server, remote to server). A pin whose session a listing supplies costs nothing; it matters when the rail would otherwise drop the row: the user switches Team → Mine with a teammate's session open, the ended prefix moves past it, or it was never loaded. A hit can be a teammate's session under Mine, an ended session past the loaded prefix, or both. Today `BuildServer` builds the read-only workspace only from a `server:{sessionId}` row already in the directory; under Mine, `MergeServerLane` also drops teammates' daemon rows, and every ended row is taken as the viewer's own

- **The pin is an exemption inside the merge, not a row added after it.** The pinned session id is an input to `AgentDirectory`'s merge: its daemon rows are kept through the Mine scope filter, the state filter and the page cut, and the ordinary daemon/server suppression then chooses its winner. So the same hit opens the same workspace, with one rail row, under Mine and under Team, and the filter itself does not change
- **A server row for a pinned session no listing supplies** comes from a pin source of its own, hydrated as below the moment the listing stops supplying it (the last listing row stands until the summary answers), or at once when it was never listed. It never comes from the ended prefix, and it carries its real owner: ownership is decided as for any server row, by comparing the owner id with the signed-in account's ids, so a teammate's pinned row shows the teammate's initials under Mine
- **Building that row.** The listing has no session-id filter, so the row comes from `GET api/sessions/{id}/summary` (`ActivitySession`), which is authoritative for everything it carries: session id, slug, title, status, vendor, owner user id, branch, cwd, started and ended times, `LastEventAt` as last activity, and the primary repository hash and owner/name from `Repositories`. The search hit (or the last listing row) contributes only presentation the summary lacks: owner username, display name and avatar, used as fallbacks and replaced by a listing row's when one arrives. Nothing the row needs for identity, ownership or timing comes from the hit, whose owner id and start time are optional
  - `access_level` is not on the summary and is left null: the read-only workspace then acquires its lease, and the server's verdict decides (a session below full access lands Denied with the generic note). `stale` is false
- **Freshness.** While a pin is held and no listing supplies its session, the pin re-reads the summary on `ActiveSessionAdded`, `ActiveSessionChanged`, `ActiveSessionRemoved`, `SessionTitleChanged` or `SessionAccessChanged` for that session id, on every lane reconnect, and on the sessions service's reconciliation cadence, so a teammate's live session that ends shows ended without its push. Each read is numbered within the pin's lifetime and only the newest-issued one publishes, so an older active summary never overwrites a newer ended one; a read from an earlier pin lifetime publishes nothing. `SessionDeleted` for it, or a summary read answering 403/404, drops the pin and tombstones the session id as the listing reads already do (`_tombstones`), invalidating every read in flight for it, and the workspace closes as it does for any deleted session. These reads run in `ServerSessionsService` (or beside it) and publish into the directory as a source; none runs inside the directory's recompute
- **One row, not two.** When a listing row for the pinned session arrives it wins and the pin's own server row is withdrawn; when the listing row leaves again while the pin is still held, the pin re-reads before republishing, so an older snapshot never reappears
- **Release.** The pin is held while the session is the selected row. A navigation away releases it, and the row follows the ordinary rules. Releasing never closes a workspace by itself: the release happens only as part of a navigation that has already replaced it

### The open operation

Choosing a hit is an open intent. The page holds at most one: choosing another hit replaces it. When the rail already has a row for the session, it opens at once. Otherwise the summary read runs first, and its completion pins and opens only if all of these still hold:

- the intent is still the page's current one
- the page is still open: any user navigation, meaning Escape, a rail click, New session or another hit, closes the page or replaces the intent, which is what invalidates it
- the identity generation is unchanged
- no `SessionDeleted` or `SessionAccessChanged` for that session arrived since the read was issued. A deletion fails the open as unavailable; an access change re-issues the read under the same intent

Automatic workspace transitions under the cover do not invalidate the intent. A covered session completing and handing off goes through `SwapTo` and bumps `NavigationGate`, but it is not the user navigating, so admission does not use `NavigationGate`, and those transitions do not dismiss the page either. A superseded completion publishes nothing. While the read runs, the row shows a spinner and the page stays open. Failures leave the page open, with nothing pinned, and show one line in place of the list:

- 401: the sign-in prompt
- 403 or 404: "This session is no longer available"
- anything else: "Couldn't open this session · try again"

Opening a hit selects it in the rail and expands its group, as `NotifySessionOpened` does today

## Identity

Search results, pins and pending opens belong to one signed-in account. They carry the identity generation the sessions service already keeps (`RunAsync`, `ClearForNewAccount`): it advances on a lane reconnect and on the subject changing to another account

- **A different account** clears the page's results and highlight, cancels the in-flight search and any pending open, and drops every pin before any read for the new account is issued. A late response from the old generation is dropped on arrival, and a failed replacement read leaves an empty surface, never the previous account's hits or rows
- **The same account signing in again after a lapse** keeps the query, the tab and the selected pin. The search is re-run once the lane is back, and the pin re-reads its summary

## Scope and visibility

The search covers every session the signed-in user can see on the server, regardless of the rail's Mine/Team or state filter, exactly as the web palette does. The server applies visibility per hit. (D6 filtered to the viewer's own sessions under Mine; this design does not)

## What is left to the builder

- How the cover is drawn over the main area (a sibling panel above the launcher and `WorkspaceHost` is the obvious fit)
- The DTOs for `/api/search` in `Capacitor.Remote.Models`, and a client in the app beside the existing server reads (`ServerSessionHttp` / `AuthenticatedServerReads` patterns: no throws but cancellation, 401 mapped to the sign-in state)
- The debounce mechanism (System.Reactive `Throttle` is already in use)
- The shape of the pin input and pin source in `AgentDirectory` and where the pin's summary reads live
- Tests, at least:
  - view-model tests for debounce, cancellation and stale-response dropping, highlight movement and tab switching, and marker parsing
  - resolution parity: the same hit under Mine and Team for a hosted local, a hosted remote, a Failed local, an Interrupted local, an unproven local/remote twin and a session-only session opens the same workspace, and search adds no server row beside a surviving daemon row
  - the pin: a teammate's live session ends and the pin shows it ended; a rename; listing arrival and removal without an older snapshot returning; a deletion; active and ended refreshes completing in reverse order; a transcript hit without `startedAt` or owner id; Team → Mine after opening an already-listed teammate hit keeps its row, and the row survives the session's lane hand-off
  - the open operation: a delayed summary after Escape, after a rail click, and after a later hit completed first; a delayed summary while the covered workspace hands off (the open still happens); a deletion during the initial read; a full → restricted access change while a search or summary is in flight, with the late response succeeding
  - the cover: the covered workspace completes or hands off while the page is up, and Escape uncovers the result; opening search invalidates a pending launch auto-open; opening the already-open session dismisses the page
  - identity: B's replacement reads fail while A's search and summary complete late, and no hit, pin or navigation of A's survives
  - the filter dot against the defaults, and a headless view test for the rail's top row in the style of the existing rail tests
- `README.md` needs no change (no CLI surface). The docs' desktop figures harness may need the new rail top
