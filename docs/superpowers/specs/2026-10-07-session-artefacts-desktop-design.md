# Session artefacts in the desktop app (design)

GitHub: [#1098](https://github.com/kurrent-io/kcap-cli/issues/1098) (the document half). Linear: [AI-3084](https://linear.app/kurrent/issue/AI-3084); the published-page feature is [AI-2846](https://linear.app/kurrent/issue/AI-2846). The layout was agreed on mockups on 2026-10-07: an Artefacts tab in the centre, cards in the chat, and product-worded rows for kcap's own tools. Mockups: [proposals page](https://claude.ai/code/artifact/7b66e481-2a14-4bf0-b419-4d2840925a8f) (private).

## Problem

A session produces two kinds of thing a person wants to read afterwards, and the desktop app shows neither.

- **Documents**: the plan, spec and design files the agent declares through `kcap mcp plans`, or that the server rebuilds from the transcript. The PLAN section lists them as inert labels, kind plus file name, path in a tooltip. The tasks beneath argue from a document nobody can open.
- **Pages**: self-contained HTML the agent publishes through `kcap mcp artefacts`, with a stable URL, versions and an audience. The app has no code for them. A publish shows in the chat as "Called a tool" with the raw MCP name, and the URL in the result is never surfaced.

The row problem covers every kcap tool: attaching a work item, saving a memory and searching past sessions all read "Called a tool".

## Current state (what the change builds on)

- **Centre surfaces.** `WorkspaceTab { Chat, Terminal, PullRequest }` in `WorkspaceViewModel`; the segmented switch in `WorkspaceView.axaml` shows Terminal and Pull request only when relevant (`ShowsTerminalTab`, `ShowsPullRequestTab`), and `PullRequestHost` is filled lazily in code-behind with a `PullRequestReader`. `PullRequestContextViewModel` receives a callback that flips `ActiveTab`, which is how a pane row opens the reader. `RemoteSessionViewModel` has its own `RemoteTab { Chat, Terminal }` and no work-context pane.
- **Markdown.** `MarkdownView` (`Text`, `OpenLink`, `Flavor`, `RunCode`) over MarkView, used by chat rows and by the PR reader's description and comments. It disables its own scrollbars; the host scrolls.
- **No web engine.** Every URL goes through `LinkPolicy.Open` to `ShellUrlOpener`.
- **Chat tool rows.** `ToolSummary.Names` maps built-in tool names to a `ToolCategory`; any `mcp__…` name is `Other`: phrase "Called a tool", chip "Tool", puzzle-piece icon. `ToolDetail.From` picks the first of a fixed key list from the call's input. `ToolCallItem(Name, Detail, Category)` carries no result; `ChatTabViewModel` pairs `tool_call` with `tool_result` by call id to flip `Outcome`. Consecutive calls fold into a `ToolGroupItem`.
- **PLAN section.** `PlanSectionViewModel` reads `GET /api/sessions/{id}/plans` through `IPlanSource` on a 30 s poll and on `PlanActivity.PlanWritten`, which `ChatTabViewModel` raises when a `declare_plan_document`, `set_plan_tasks` or `update_plan_task` call settles without error. `PlanToolNames.IsWrite` matches by suffix because Codex records MCP tools under their bare name. `PlanDocumentDto` carries `document_key`, `kind` and `path`, never a body.
- **Document bodies.** `GET /api/sessions/{id}/plan-artifacts?chain=true` (`ISessionsApi.GetPlanArtifactsAsync`, `PlanArtifactDto`) returns declared documents merged with the server's transcript discovery: `kind`, `title`, `path`, `source` (`declared`, `repo_file`, `native_plan`, …), `content`, `content_state` (`ok`, `truncated`, `unavailable`), `content_hash`, `original_bytes`, `is_primary`. It needs full access to every session in the chain. The app has no client for it. A declaration carries the body only up to 256 KB.
- **Pages.** `ArtefactDto` (`artefact_id`, `title`, `owner_user_id`, `visibility`, `latest_version`, `updated_at`, `is_owner`, `url`) and `ArtefactDetailDto` exist in `Capacitor.Cli.Core.Http` for the CLI command. Content is served at `GET /artefacts/{id}/content?v=&theme=` behind the same auth as `/api`, with the app's base styles injected ahead of the stored bytes and a CSP that forbids any network access. The web viewer wraps it in a sandboxed iframe and a bridge for answers.
- **Session → pages.** An artefact cites sessions as `sources`; nothing lists a session's artefacts. kcap-server has `ArtefactQueries.CitingSession` and the `idx_artefact_sources_session` index, with no route and no caller.
- **The cite is unreliable.** `McpArtefactsServer.BuildPublishBody` reads `ArgParsing.ResolveSessionIdFromEnv()` (`KCAP_SESSION_ID`, then `CODEX_THREAD_ID`). Under Claude Code an MCP server never sees `KCAP_SESSION_ID`; the work-items server fell back to `HarnessRequesterContext` (#942, #944) and the artefacts server did not. A page published from a Claude session cites no session unless the agent passes `session_ids`.
- **Transcript shape.** A publish is an `AssistantToolCallsGenerated` entry whose tool name ends in `publish_artefact`, with arguments `title`, `html` or `path`, `update_id`, …; its `ToolResultReceived` text is the server's JSON passed through: `artefact.artefact_id`, `artefact.url`, `artefact.title`, `artefact.latest_version`, `artefact.visibility`. `TranscriptEnvelopes` caps `ToolResult` at 4096 characters, enough for that body.
- **Avalonia's WebView** is open source from Avalonia 12: `Avalonia.Controls.WebView` 12.1.0 (MIT, Avalonia ≥ 12), native engines (WKWebView on macOS, WebView2 on Windows, WPE WebKit on Linux), with `NavigateToString`, `WebResourceRequested`, `WebMessageReceived` and `InvokeScript`.

## Decisions

### D1 — Scope: documents and pages, both lanes, every kcap tool

Documents and pages are the two kinds. Both lanes show them: the local workspace and the remote session view, which has the chat and a transcript feed and reads the same server routes. Every kcap MCP tool gets a product-worded row; other servers' tools get a humanised fallback.

### D2 — One Artefacts tab: a master list and a reader

`WorkspaceTab` and `RemoteTab` each gain `Artefacts`. The segment shows the position only while the session has at least one document or page (`ShowsArtefactsTab`), as Pull request does; if the tab is active when the last item disappears, the view falls back to Chat.

The tab is a two-column view: a master list on the left, grouped DOCUMENTS and PAGES, each row with a kind or audience chip and a short meta; the reader fills the right. Selection uses info blue. Success green and warning orange appear only on an answer state or a drift notice, never on a row's identity.

The name is "Artefacts", as the web's nav item and `/artefacts` routes. "Outputs" was considered and rejected as unclear.

### D3 — kcap tool rows: product words, row or card

A catalogue keyed by tool-name suffix (the `PlanToolNames.IsWrite` rule, generalised) maps each kcap tool to a `ToolCategory` (new members for Artefact, Work, Memory, Session, Flow), a one/many verb phrase for `ToolSummary.Describe`, a chip label, an icon, and a detail selector: an argument key, or a path into the result. Rows read "Attached work item · AI-3084", "Searched sessions · artefact app · 10 hits", "Task done · Document reader", "Saved memory · slug", and "Waiting for answers · 1 of 3 so far" while running.

A call that created something the user can open renders as a `ToolCardItem` rather than a `ToolCallItem`:

| Tool | Card |
| --- | --- |
| `publish_artefact` | Published page · title · v3 · Org. Open, Copy link. "Updated page" when `latest_version` > 1. |
| `declare_plan_document` | Declared design doc · file name. Open. |
| `start_review_flow`, `start_flow` | Started code review flow · reviewer. Open on web. |
| `start_agent` | Started hosted agent · first line of the prompt. Open selects it in the rail. |

The card sits inside the tool group in sequence and a folded group keeps it visible. Its details come from the result, so it renders as a row until the result settles. A failed call keeps the group's existing behaviour: the group opens and the row shows the error.

Unknown MCP tools read "Server · tool name", humanised from the `mcp__<server>__<tool>` shape, with the first string argument.

### D4 — Three ways in, one reader

A card's Open, a PLAN document row in the pane, and a master-list row all select the same item and switch to the tab. The pane adds no section; it gains one summary row, "ARTEFACTS · 3 documents · 2 pages", that opens the tab. PLAN keeps its document rows as shortcuts: hover and pointer like the pane's other clickable rows, and the open one carries the purple location mark.

### D5 — Documents: the server snapshot, read with the tab, with honest state

The Documents group lists what `plan-artifacts?chain=true` returns, deduplicated against the ledger's documents by `content_hash` then path, ordered plan, spec, design, other, then primary first. The read runs when the tab first shows and on `PlanWritten`; the body rides the same response, so opening a document costs no second fetch. A row shows kind, file name, declared age, and a chip when `content_state` is not `ok`.

`DocumentReader`: a header (kind chip, file name, path, declared age, size), a notice line, a `MarkdownView` with `Flavor=GitHub` inside a `ScrollViewer`, and a Copy path action. `truncated` renders what there is under a notice naming the cap; `unavailable` renders the header and the notice with no body. For a local session with a known worktree the reader hashes the working copy at the declared path once per open and shows "Working copy has changed since this was declared" when it differs; it never shows the working copy in place of the snapshot. Links go through `LinkPolicy`.

The route needs full access; at overview level the group is hidden, not an error.

### D6 — Pages: two sources, as PLAN has

The Pages group is fed by two sources. The transcript: a settled, non-error result for a call whose name ends in `publish_artefact`, parsed for `artefact_id`, `url`, `title` and `latest_version`. The server: a new `GET /api/sessions/{id}/artefacts` returning the artefacts citing that session that the viewer can see, in `ArtefactDto` shape. The transcript is the live trigger and the first row; the server read is the truth that also covers remote sessions, subagent publishes and versions published from elsewhere. The read runs when the tab first shows, after a transcript trigger with the same settle delay PLAN uses, and on the pane's poll while the tab is visible. Rows merge by `artefact_id`.

The route is worth building only once the cite is reliable: `McpArtefactsServer` adopts the `HarnessRequesterContext` fallback the work-items server has. Both ship in the same slice. Until the route exists the group shows transcript-detected pages only.

### D7 — Page body: the engine, contained; a card without it

`PageReader`: a header (title, version, audience chip, updated age, owner when not the viewer), the actions Copy link, Open in browser and Share (which opens the web viewer), and the body.

With an engine: the app fetches `GET /artefacts/{id}/content?v={n}&theme=dark` with the bearer token through the existing server HTTP client and hands the bytes to `NativeWebView.NavigateToString`. The document runs at an opaque origin with no cookies, and the app holds none. Every `WebResourceRequested` is denied; a `<meta http-equiv="Content-Security-Policy">` mirroring the server's header is injected after `<head>`; a new-window request goes through `LinkPolicy`. Scripts stay on because pages use them.

Without an engine (no WebView2 runtime, no WPE libraries, or the control fails to create): the header stays and the body is a card with the title and Open in browser. The reader decides once per process.

Answers: a page with a response schema talks to a parent frame the app does not provide. When the detail says the page collects answers, the header shows "This page collects answers. Respond in the browser." An in-app bridge shim is a later slice.

### D8 — Header budget

The segment is the one element of the centre header that grows (Chat, Terminal, Pull request, Artefacts, and a Diff tab is expected). Everything to its right compresses to icon buttons with tooltips: "Open in web" becomes the external-link glyph the PR card uses, in a `kcapGhost kcapIcon` button. Stop keeps its word. Tab labels are one or two short words.

### D9 — What this deliberately does not do

- No editing of a document and no opening in an external editor.
- No in-app share control, version picker or answer submission in the first delivery.
- No rail chip for sessions with artefacts.
- No strip under the header and no floating reader window.
- No change to how the pull request reader opens.

## Component changes (implementation map)

App (`src/Capacitor.App`):

- `ViewModels/WorkspaceViewModel.cs`, `ViewModels/RemoteSessionViewModel.cs`: the `Artefacts` tab, `ShowsArtefactsTab`, `ShowArtefactsCommand`, fallback to Chat.
- `Views/WorkspaceView.axaml(.cs)`, `Views/RemoteSessionView.axaml(.cs)`: the segment position, a lazily created `ArtefactsView` host, the "Open in web" icon button.
- New `ViewModels/ArtefactsTabViewModel.cs` (groups, selection, reads), `DocumentRow.cs`, `PageRow.cs`, `DocumentReaderViewModel.cs`, `PageReaderViewModel.cs`; `Views/ArtefactsView.axaml`, `DocumentReader.axaml`, `PageReader.axaml`, and a `PageHost` that holds the engine or the fallback card.
- `ViewModels/ToolSummary.cs`, `ViewModels/ToolDetail.cs`, `Views/ToolCategoryIcons.cs`: the new categories; a new `ViewModels/KcapToolCatalogue.cs` holding the table. `ToolCardItem` in `ViewModels/ChatItems.cs`, its template in `Views/ChatTabView.axaml`, and `ChatTabViewModel` building cards and filling them from results.
- `ViewModels/PlanActivity.cs` generalised to `ArtefactActivity`, raising `PlanWritten` and `PagePublished`.
- `ViewModels/PlanSectionViewModel.cs`, `Views/WorkContextView.axaml`: clickable document rows, the open mark, the summary row.
- `Services/`: `IPlanArtifactSource` over `ISessionsApi.GetPlanArtifactsAsync`; `ISessionArtefactSource` over the new route; `IArtefactContentSource` for the bearer fetch of a page body. Registrations in `App.axaml.cs`.
- `Capacitor.App.csproj` and `Directory.Packages.props`: `Avalonia.Controls.WebView` (slice 4).

Core and CLI:

- `src/Capacitor.Cli.Core/Http/SessionsApi.cs`: `GetSessionArtefactsAsync`.
- `src/Capacitor.Cli/Commands/McpArtefactsServer.cs`: the `HarnessRequesterContext` fallback for the ambient session.

Server (kcap-server):

- `GET /api/sessions/{id}/artefacts` over `ArtefactQueries.CitingSession`, filtered by the visibility oracle.

## Delivery

1. **Tool rows and cards.** The catalogue for every kcap MCP tool, and cards for the four producers; Open goes to the browser until the tab exists. App only; visible in every session from the first release.
2. **Artefacts tab, documents.** The tab, the Documents group, `DocumentReader`, the pane summary row and PLAN shortcuts. App only. Closes #1098.
3. **Pages.** The Pages group from the transcript trigger and the new route, the MCP cite fix, `PageReader` with the fallback body. Server, CLI and app.
4. **Embedded page.** The WebView package, the contained load, the fallback. App, plus a review of the containment.
5. **Later, each its own issue.** The answers bridge shim, the share control, a version picker, a rail chip.

## Behavior notes and accepted limitations

- A transcript-detected page row shows the title and version from the result; owner and audience arrive with the server read.
- A page the viewer has lost access to drops out of the server read; a transcript row for it keeps its title, and Open in browser reaches the web's not-available page. Accepted: it is what happened.
- A document over 256 KB, or one that is not UTF-8, has no body on any lane.
- The drift check reads the working copy once per open and does not watch the file.
- The remote view has no PLAN section, so the tab and the cards are its two ways in.
- The engine needs WPE libraries on Linux and the WebView2 runtime on Windows; the fallback covers both.
- The answers notice needs the artefact detail to say whether the version carries a response schema. The CLI's `ArtefactDetailDto` reads only `artefact` and `grants`; the server's detail response has to be checked for that field and extended if it is absent, as part of slice 3.

## Testing

- Catalogue: suffix matching for the Claude and Codex spellings; one/many phrases; detail from an argument and from a result; the unknown-MCP fallback.
- Chat: a `publish_artefact` call renders as a row until its result, then as a card with Open and Copy link; `latest_version` 2 reads "Updated page"; a failed publish shows the error row; a folded group keeps its card visible.
- Tab visibility: appears with the first document or page; hides and falls back to Chat when none remain; the remote view behaves the same.
- Documents: dedupe, order, state chips, truncated and unavailable rendering, the drift notice for equal hash, different hash and missing file, and the group hidden at overview access.
- Pages: a transcript row then the server merge by `artefact_id`; the server read on tab show, after a trigger, and on the poll; a version bump updates the row.
- `PageReader`: content fetched with the bearer token and the theme parameter; every resource request denied; the CSP meta injected once; a new-window request reaches `LinkPolicy`; the fallback when creation fails; the answers notice.
- Header: the icon button carries the tooltip and the same command; five segment positions fit at the window's minimum width.
- Pane: the summary row opens the tab; a PLAN row opens its document; the open mark follows the selection.
- MCP: `BuildPublishBody` cites the session from the harness context when `KCAP_SESSION_ID` is absent.

## Out of scope

- Editing documents; opening them in an external editor.
- In-app answers, share and version picker (slice 5).
- A rail indicator.
- Web UI changes beyond the server route.
- Other vendors' artefact-like outputs.
