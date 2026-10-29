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

The panel starts collapsed on each launch (my call: persisting it is cheap, but the point of the change is a quieter rail). The magnifier and filter glyphs are new `AppIcon` kinds; the launcher model picker's magnifier path is the reference shape

## The search page

### Hosting and navigation

The page occupies the main area beside the rail, where the launcher and the session workspace live. It is not an `ISessionWorkspace` (it has no agent id): the builder hosts it as a third state of the main area. Opening it keeps a reference to whatever was showing (launcher or workspace) without tearing that workspace down; Escape returns to it. Selecting a hit navigates to the session as a rail click does, which replaces both

While the page is showing, the rail's search field shows the current query in place of the placeholder (as in the mockup), and the rail keeps its selection: the previous workspace is still alive until a hit replaces it

Each opening starts fresh: empty query, Sessions tab (my call; Linear does the same)

### Shortcut

⌘K on macOS opens the page from anywhere in the main window, including while the terminal has focus. Off macOS, Ctrl+K yields to a focused terminal, as Ctrl+R already does (`RefreshUnlessTerminalFocused`), because Ctrl+K is a terminal key there. Pressing the shortcut while the page is open focuses and selects the query text. It is mirrored as a native menu item with the gesture, next to the existing ones in `AppMenuBar`

### Query and requests

- `GET {server}/api/search?q={query}&mode={sessions|transcripts}&limit=20`, through the app's authenticated `ICapacitorHttpClient`, bearer token as every other read. `mode` omitted means sessions; the builder may send it explicitly
- A request goes out 150 ms after the last keystroke, matching the web palette. A query shorter than 3 characters after trimming sends nothing and shows "Type at least 3 characters"
- Each new request cancels the previous one, and a response for anything other than the current (query, tab) is discarded on arrival
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

A hit opens exactly what a rail click on that session would open. Hits name a session id; a transcript hit's agent id names a subagent inside it and is not used for navigation. The app resolves the session id through the directory, as `DaemonRowFor` matches daemon rows to sessions: a live agent hosted on this machine opens its local workspace, one on another of the user's daemons opens the remote host, and anything else opens the read-only workspace, on Overview when ended and Chat when live, as the past-sessions design defines. Calling `OpenSession(sessionId, AgentOrigin.Server)` directly is not enough, because daemon rows are keyed by agent id and an explicit lane skips that resolution

**When the rail has no row for the session**, the hit is **pinned** to the rail (owner's decision). `BuildServer` in `App.axaml.cs` today builds the read-only workspace only from an `AgentRow` already in the directory under `server:{sessionId}`, and the rail's scope decides what is there: under Mine, live server rows come from the `owner=me` read and every ended row is taken as the viewer's own. A hit can be a teammate's session, an old one past the loaded prefix, or both. Pinning means:

- The app reads `GET api/sessions/{id}/summary` (the listing has no session-id filter) and builds a server row from it with what `AgentRow.FromServer` needs: title, vendor, status, owner, repository, times
- The row comes from its own source in the directory, not from the ended prefix, and carries its real owner: a teammate's pinned row shows the teammate's initials under Mine. Ownership is decided as for every other server row, by comparing the owner id with the signed-in account's ids
- The pinned row is exempt from the scope filter, the state filter and the page cut, and shows in the group it belongs to under the current grouping
- It stays while it is the selected row. When the user opens anything else, the pin is released and the row follows the ordinary rules: it disappears if the scope or loaded set would not show it
- If the same session later arrives through the ordinary listing, that row and the pin are one row, not two
- The open workspace keeps its usual lifecycle: the end-of-session hand-off and the close-on-removal watch see the pinned row as they see any server row, and releasing the pin while the workspace is open never closes it (the pin is released only by a navigation away)

When the summary read refuses (deleted, no longer visible), the search page stays open and shows "This session is no longer available" in place of the list's error line

Opening a hit selects it in the rail and expands its group, as `NotifySessionOpened` does today

## Scope and visibility

The search covers every session the signed-in user can see on the server, regardless of the rail's Mine/Team or state filter, exactly as the web palette does. The server applies visibility per hit. (D6 filtered to the viewer's own sessions under Mine; this design does not)

## What is left to the builder

- How the main area hosts the third state (a sibling panel or a template in `WorkspaceHost`) and how `MainWindowViewModel` tracks the page and what was showing before it
- The DTOs for `/api/search` in `Capacitor.Remote.Models`, and a client in the app beside the existing server reads (`ServerSessionHttp` / `AuthenticatedServerReads` patterns: no throws but cancellation, 401 mapped to the sign-in state)
- The debounce mechanism (System.Reactive `Throttle` is already in use)
- The shape of the pinned-row source in `AgentDirectory`
- Tests: view-model tests for debounce, cancellation and stale-response dropping, highlight movement and tab switching; marker parsing; opening a hit outside the loaded set; the filter dot against the defaults. A headless view test for the rail's top row in the style of the existing rail tests
- `README.md` needs no change (no CLI surface). The docs' desktop figures harness may need the new rail top
