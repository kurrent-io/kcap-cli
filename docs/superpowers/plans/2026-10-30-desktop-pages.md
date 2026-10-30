# Desktop Pages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** List a session's kcap Pages in the desktop Artefacts tab (AI-4775, #2154), then render and answer them inside the app (AI-4825, #2188).

**Architecture:** One bearer-carrying `PagesClient` in Core reads the server's page routes. The Artefacts tab gains a second single-flight read (pages) beside the plans read, with its own "unavailable" state. A `PageViewer` hosts Avalonia's `NativeWebView` showing an app-owned host page that frames the fetched HTML in a sandboxed `srcdoc` iframe and relays the `{kcap:'v1'}` bridge to .NET, which makes the REST calls.

**Tech Stack:** .NET 10, Avalonia 12.1.2, ReactiveUI, `Avalonia.Controls.WebView` 12.1.1 (MIT), TUnit, Avalonia.Headless.

**Spec:** Linear AI-4775 and AI-4825 (GitHub #2154, #2188) — read both issue bodies; server reference is `kcap-server/src/Capacitor.Ui/wwwroot/js/artefact-bridge.js` and `Capacitor.Artefacts/ArtefactContentSecurity.cs`.

## Global Constraints

- Two PRs: Tasks 1–3 ship as AI-4775 on `tonyyoung/ai-4775-session-pages-in-artefacts`; Tasks 4–8 as AI-4825 on a branch stacked on it.
- Routes: `GET /api/sessions/{id}/artefacts` (list, `ArtefactListDto`), `GET /api/artefacts/{id}` (detail), `GET /artefacts/{id}/content?v={n}&theme=dark`, `POST /api/artefacts/{id}/responses` body `{version, payload}`, `GET /api/artefacts/{id}/responses?version={n}&mine=true`, `GET /api/artefacts/{id}/results?version={n}`.
- The app is Dark-only (`RequestedThemeVariant="Dark"`): always request `theme=dark`.
- Every outbound link goes through `LinkPolicy.Open`; nothing navigates the WebView away from the host page.
- Frame sandbox exactly `allow-scripts allow-popups`; injected CSP meta is the server's `ArtefactContentSecurity.ContentSecurityPolicy` minus `frame-ancestors` and `sandbox` (both ignored in `<meta>`).
- Bridge limits copied from the web: envelope `'v1'`, max message 256 KB (`JSON.stringify(data).length`), results poll 5000 ms, origin `"null"`, source = our frame.
- No new IL2026/IL3050 from `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release` nor from publishing `src/Capacitor.App`.
- Repo CLAUDE.md comment and test rules apply (scarce comments, `TempDir`, no env-absence asserts).

## Review Focus

- Older server without `/api/sessions/{id}/artefacts` (404) → "Pages unavailable", never "No pages yet" (Task 2 test).
- Sign-out mid-session → pages list cleared and the cache dropped, like plans (Task 1/2 tests).
- Page link in chat for another server's host → still parsed by id and read from this profile's server; a 404 shows "not found" (Task 7 test).
- A page that posts a 300 KB message or posts from a nested frame → dropped silently (Task 6 host-page test).
- WebView runtime missing (Linux without WebKitGTK, Windows without WebView2) → viewer falls back to "Open in browser" without throwing (Task 5 test).

---

## PR 1 — AI-4775: session pages in the Artefacts tab

### Task 1: `PagesClient` session list and `ServerPageSource`

**Files:**
- Create: `src/Capacitor.Cli.Core/Pages/PagesClient.cs`, `src/Capacitor.Cli.Core/Pages/SessionPagesRead.cs`
- Create: `src/Capacitor.App/Services/IPageSource.cs`, `src/Capacitor.App/Services/ServerPageSource.cs`
- Modify: `src/Capacitor.App/App.axaml.cs:~1017` (construct beside `planArtifacts`), `src/Capacitor.App/Services/ServerClients.cs` (dispose + invalidate)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Pages/PagesClientTests.cs`, `test/Capacitor.App.Tests.Unit/ServerPageSourceTests.cs` (mirror `ServerPlanArtifactSource` tests if present)

**Interfaces:**
- Produces: `public sealed class PagesClient(HttpClient http, string serverUrl)` with `Task<SessionPagesRead> ListForSessionAsync(string sessionId, CancellationToken ct)`; `public sealed record SessionPagesRead(SessionPlansReadKind Kind, IReadOnlyList<ArtefactDto> Pages) { public static SessionPagesRead Of(SessionPlansReadKind kind); }` (Pages empty unless Ready).
- Produces: `public interface IPageSource { Task<SessionPagesRead> ReadAsync(string sessionId, CancellationToken ct); SessionPagesRead? Cached(string sessionId); }`; `ServerPageSource : IPageSource, IAsyncDisposable` with `InvalidateAuthentication()` — same shape and cache rules as `ServerPlanArtifactSource`.

- [ ] **Step 1: Failing tests** — `PagesClientTests` with a stub `HttpMessageHandler`: 200 `{"artefacts":[…]}` → Ready with two pages; 200 `{"artefacts":[]}` → Ready, empty; 401 → SignedOut; 403/404 → Unavailable; 500 and `HttpRequestException` → Unreachable; non-canonical session id → Unavailable with no request sent; request path is `/api/sessions/{id}/artefacts`.
- [ ] **Step 2: Run, expect FAIL** — `~/.dotnet/dotnet run --project test/Capacitor.Cli.Core.Tests.Unit -- --treenode-filter "/*/*/PagesClientTests/*"`
- [ ] **Step 3: Implement** `PagesClient.ListForSessionAsync` exactly as `PlanArtifactsClient.ReadAsync` totalizes (same `IsTransient`, `WorkContextIds.CanonicalSessionId`), reading `CapacitorJsonContext.Default.ArtefactListDto`. Then `ServerPageSource` copying `ServerPlanArtifactSource` (`LastReads` capacity 32), wire it in `App.axaml.cs` and `ServerClients` (new trailing-but-one ctor parameter `pages`, per the "new parameter second from last" norm).
- [ ] **Step 4: Run, expect PASS**; build `src/Capacitor.App`.
- [ ] **Step 5: Commit** `Read a session's pages from the server (#2154)`

### Task 2: Pages in `ArtefactsTabViewModel`

**Files:**
- Create: `src/Capacitor.App/ViewModels/PageRow.cs`
- Create: `src/Capacitor.Cli.Core/Pages/PageToolNames.cs`
- Modify: `src/Capacitor.App/ViewModels/ArtefactsTabViewModel.cs`, `src/Capacitor.App/ViewModels/PlanActivity.cs`, `WorkspaceViewModel.cs:230-239` and `RemoteSessionViewModel.cs:292-298` (pass the source; the `ShowsArtefactsTab` subscriptions watch `HasAnyArtefacts`/`IsLoading` instead of `HasAny`/`IsLoading`), the `IPageSource` through their ctors and `App.axaml.cs`
- Test: `test/Capacitor.App.Tests.Unit/ArtefactsTabViewModelTests.cs` (extend), `WorkspaceViewModelTests.cs`, `RemoteSessionViewModelTests.cs`, `FakePageSource.cs` (mirror `FakePlanArtifactSource`), `test/Capacitor.Cli.Core.Tests.Unit/Pages/PageToolNamesTests.cs`

**Interfaces:**
- Consumes: `IPageSource`, `SessionPagesRead` (Task 1).
- Produces: `PageRow(string artefactId, string title, int latestVersion, DateTimeOffset updatedAt, string url, bool isOwner)` with `UpdatedLabel` (relative, reuse the app's existing relative-time formatter if one exists); `ArtefactsTabViewModel.Pages : IAvaloniaReadOnlyList<PageRow>`, `HasPages`, `HasAnyArtefacts => HasAny || HasPages`, `IsLoading` true while either the documents or the pages read is unsettled, `PagesNote : string?` (`null` when rows show; `"Loading pages…"`, `"No pages yet."`, `"Pages unavailable on this server."`, `"Sign in to see pages."`), `OpenPageCommand : ReactiveCommand<PageRow, Unit>`, `event Action<PageRow>? PageOpenRequested`; ctor gains `IPageSource? pages` (second from last). `PageToolNames.IsPublish(string? toolName)` matching `publish_page` the way `PlanToolNames.IsWrite` matches; `PlanActivity.PagePublished` event raised on a settled, non-error `publish_page` result.

- [ ] **Step 1: Failing tests** —
  - `Pages_listed_for_the_session_newest_first`: fake returns pages updated at t1, t2 → `Pages` titles `[t2-page, t1-page]`, `PagesNote == null`.
  - `Empty_pages_read_says_no_pages_yet`: Ready, [] → `PagesNote == "No pages yet."`.
  - `Unavailable_pages_read_is_not_an_empty_list`: Unavailable → `"Pages unavailable on this server."`, `Pages` empty.
  - `Signed_out_clears_pages`: Ready then SignedOut → empty, `"Sign in to see pages."`.
  - `Unreachable_keeps_the_last_pages`: Ready(2) then Unreachable → still 2, `LastReadFailed` true.
  - `Switching_session_drops_a_late_pages_result`: gated read for s1, switch to s2, release s1 → `Pages` reflect s2 only.
  - `Publish_page_result_refreshes_pages`: `PlanActivity.Apply` a `publish_page` call+result → a second `ReadAsync` call.
  - `Open_page_without_viewer_opens_the_browser`: no `PageOpenRequested` subscriber → fake `IUrlOpener` receives `row.Url`.
  - `PageToolNamesTests`: `mcp__kcap-pages__publish_page` and `publish_page` true; `republish_pages` false.
  - `Pages_only_session_shows_the_artefacts_tab` in both `WorkspaceViewModelTests` and `RemoteSessionViewModelTests`: documents Ready/[] and pages Ready/[one] → `ShowsArtefactsTab` true after both settle; both empty → false.
- [ ] **Step 2: Run, expect FAIL** — `~/.dotnet/dotnet run --project test/Capacitor.App.Tests.Unit -- --treenode-filter "/*/*/ArtefactsTabViewModelTests/*"`
- [ ] **Step 3: Implement** a second `SingleFlightReader<string, SessionPagesRead>` (`_pageReads`) switched, refreshed, cleared and torn down alongside `_reads`; `LastReadFailed` is true when either last read was Unreachable. `Refresh()` refreshes both. `OnPagePublished` refreshes pages only. Open: raise `PageOpenRequested` if subscribed, else `LinkPolicy.Open(opener, row.Url)`. Update `EmptyReaderNote`'s "No documents yet." to account for pages: `HasAny || HasPages ? "Select a document to read it." : …`.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** `List a session's pages in the Artefacts tab (#2154)`

### Task 3: Pages section in `ArtefactsView`

**Files:**
- Modify: `src/Capacitor.App/Views/ArtefactsView.axaml`
- Test: `test/Capacitor.App.Tests.Unit/ArtefactsViewSmokeTests.cs` (create, or extend the workspace smoke tests that already render the tab)

**Interfaces:** Consumes `Pages`, `PagesNote`, `OpenPageCommand` (Task 2).

- [ ] **Step 1: Failing headless test** `Pages_section_renders_rows_and_note`: host the view with a VM whose fake source returns one page "Release checklist" → a `Button.artefactRow` with that text under a "PAGES" header; with Unavailable → a TextBlock "Pages unavailable on this server.".
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** a "PAGES" header + `ItemsControl` under the documents list in the left column (same row style; second line "Page · v{LatestVersion} · {UpdatedLabel}", `ToolTip.Tip` = Url, `AutomationProperties.Name` = Title) and a muted `TextBlock` bound to `PagesNote`, visible when non-null.
- [ ] **Step 4: Run, expect PASS**; launch the app (`DOTNET_ROOT=~/.dotnet`) against a session with a page and click it → browser opens the page.
- [ ] **Step 5: Commit** `Show a session's pages beside its documents (#2154)`; open PR 1 (template: `Closes #2154 — AI-4775`).

---

## PR 2 — AI-4825: render pages in the app

### Task 4: Page content and response calls on `PagesClient`

**Files:**
- Create: `src/Capacitor.Cli.Core/Pages/PageContentRead.cs`, `src/Capacitor.Cli.Core/Pages/PageBridgeReply.cs`
- Modify: `src/Capacitor.Cli.Core/Pages/PagesClient.cs`, `src/Capacitor.App/Services/IPageSource.cs`, `ServerPageSource.cs`
- Test: `PagesClientTests.cs` (extend)

**Interfaces:**
- Produces on `PagesClient` and `IPageSource`:
  - `Task<PageContentRead> ReadContentAsync(string artefactId, int? version, CancellationToken ct)` → `abstract record PageContentRead { Found(ArtefactDto Page, int Version, string Html); NotFound; SignedOut; Unreachable }`. Reads the detail first, then content pinned to `version ?? LatestVersion`, `theme=dark`. 404 on either → NotFound; 403 → NotFound.
  - `Task<PageBridgeReply> SubmitAsync(string artefactId, int version, JsonElement payload, CancellationToken ct)`, `Task<PageBridgeReply> MineAsync(string artefactId, int version, CancellationToken ct)`, `Task<PageBridgeReply> ResultsAsync(string artefactId, int version, CancellationToken ct)` → `sealed record PageBridgeReply(int Status, JsonElement? Body)`; Status 0 for a transport failure. The client does not interpret 409/429; the bridge does. The results ETag is the body's `etag` field (the server sends no ETag header); no `If-None-Match`, no 304 handling.
  - `Task<PageResultsAccess> ResultsAccessAsync(string artefactId, ArtefactDto page, CancellationToken ct)` → `enum PageResultsAccess { Visible, Hidden }`: Visible when `page.IsOwner`, else one `ResultsAsync` for `page.LatestVersion` returning 200 with `mode != "owner"`; any other answer is Hidden. This is the web viewer's rule (`CanSeeResults => resultsShared || IsOwner`, `resultsShared = results.Mode != Owner`).
- [ ] **Step 1: Failing tests**: content read sends `?v=3&theme=dark` after a detail naming `latest_version: 3`; explicit `version: 2` reads v2; 404 detail → NotFound and no content request; submit posts `{"version":3,"payload":{…}}`; 409 submit → `Status == 409` with the server's body; results path carries `?version=3`; mine path `?version=3&mine=true`; owner → Visible with no results request; non-owner with results `mode:"owner"` → Hidden; non-owner with `mode:"aggregate"` → Visible; non-owner results 404 → Hidden.
- [ ] **Step 2–4: Run FAIL, implement, run PASS.** JSON bodies via `CapacitorJsonContext` (add a `PageResponseBody(int Version, JsonElement Payload)` type to it); no reflection serialization.
- [ ] **Step 5: Commit** `Read page content and answers with the app's token (#2188)`

### Task 5: WebView adoption spike + `PageViewer` shell

**Files:**
- Modify: `Directory.Packages.props` (`Avalonia.Controls.WebView` `12.1.1`), `src/Capacitor.App/Capacitor.App.csproj`
- Create: `src/Capacitor.App/Views/PageViewer.axaml(.cs)`, `src/Capacitor.App/ViewModels/PageViewerViewModel.cs`, `src/Capacitor.App/Services/IWebViewAvailability.cs`
- Test: `test/Capacitor.App.Tests.Unit/PageViewerViewModelTests.cs`

**Interfaces:**
- Produces: `PageViewerViewModel(IPageSource source, string artefactId, string browserUrl, IUrlOpener opener, TimeProvider time, bool webViewAvailable)` — `browserUrl` is the row's `Url`, or for a chat link the link itself after Task 7's origin check, so the no-WebView path can open the browser without any read; with `State` (`Loading | Ready | NotFound | SignedOut | Unreachable | NoWebView`), `Title`, `Html` (host document, set when Ready), `OpenInBrowserCommand`, `ReloadCommand`, `Task LoadAsync(CancellationToken)`; `IWebViewAvailability.IsAvailable` (macOS: true; Windows: WebView2 runtime present; Linux: WebKitGTK loadable — use whatever the package exposes, else a guarded native probe).
- [ ] **Step 1: Spike (no commit of throwaway code)**: add the package, put a `NativeWebView` in a scratch window, `NavigateToString` a page with an iframe `srcdoc`; confirm on macOS (a) it renders, (b) the script→host message API name and the host→script call (`InvokeScript` or equivalent), (c) a navigation/new-window cancel hook exists, (d) from inside the sandboxed srcdoc iframe and a frame nested in it, whether the native message channel object is reachable and whether a direct call arrives in .NET, and whether the package exposes the sending frame (WKWebView `frameInfo.isMainFrame`, WebView2 frame events). Repeat (d) on Windows. Record the results in the PR description; Task 6's nonce check is required whatever (d) shows. `dotnet publish src/Capacitor.App -c Release` and the CLI publish: grep `IL[23][01][0-9]{2}` — expect none new. Record the API names at the top of `PageViewer.axaml.cs` usage, not in a comment.
- [ ] **Step 2: Failing tests**: Found → `State == Ready`, `Title` from the record, `Html` contains the page HTML inside `srcdoc` (HTML-attribute-escaped) and `sandbox="allow-scripts allow-popups"`; NotFound → `State == NotFound`; `webViewAvailable: false` → `NoWebView` and no content read; `OpenInBrowserCommand` → opener gets the page's `Url`.
- [ ] **Step 3: Implement** the view model (host document built by Task 6's `PageHostDocument.Build`; stub it here returning the srcdoc frame only) and the view: `NativeWebView` when Ready, text states otherwise, header with title and "Open in browser". Popups/new windows and any navigation other than the initial host load are cancelled and routed to `LinkPolicy.Open`.
- [ ] **Step 4: Run PASS.**
- [ ] **Step 5: Commit** `Add an in-app page viewer on Avalonia's WebView (#2188)`

### Task 6: Host page and bridge relay

**Files:**
- Create: `src/Capacitor.App/Assets/page-host.js` (embedded resource), `src/Capacitor.App/Services/PageHostDocument.cs`, `src/Capacitor.App/Services/PageBridge.cs`
- Test: `test/Capacitor.App.Tests.Unit/PageBridgeTests.cs`, `PageHostDocumentTests.cs`; a shared fixture `test/Capacitor.App.Tests.Unit/Fixtures/bridge-v1.json` listing the envelope types and fields from `artefact-bridge.js`

**Interfaces:**
- Produces: `PageHostDocument.Build(string pageHtml, string nonce) : string` — host HTML with the CSP-meta-prefixed page in `<iframe sandbox="allow-scripts allow-popups" srcdoc="…">` and `page-host.js` inlined with the nonce bound inside its closure (never on `window`, the DOM, or the iframe). `PageBridge(IPageSource source, ArtefactDto page, int version, PageResultsAccess access, string nonce, Func<string, Task> postToPage, TimeProvider time)` with `Task OnNativeMessageAsync(string raw)`, `void Start()`, `ValueTask DisposeAsync()`. The view generates the nonce per load with `RandomNumberGenerator` (32 bytes, base64url) and obtains `access` from `ResultsAccessAsync` before constructing the bridge.
- Native-channel provenance: `page-host.js` forwards `{nonce, data}`; `OnNativeMessageAsync` drops (silently) anything not parsing to that shape, with a nonce that does not match by `CryptographicOperations.FixedTimeEquals`, or — where Task 5 found the package exposes it — not from the main frame. The opaque-origin iframe cannot read the host page's closure, so a page calling the native channel directly carries no valid nonce.
- `page-host.js`: listens for `message`, applies the four checks (source is the iframe's `contentWindow`, `origin === 'null'`, `kcap === 'v1'` with string `type`, size ≤ 256 KB), forwards accepted `data` with the nonce over the WebView's host channel; exposes a host→page delivery function which posts `{kcap:'v1', ...message}` to the frame with target `'*'`. Sets the iframe content only after the listener is attached (the web bridge's ordering).
- `PageBridge` mirrors `artefact-bridge.js`: `ready` → `init {version, theme:'dark', viewer:{name}, prior}` then results if allowed; `submit` → ack accepted with `response_id`, else ack `{accepted:false, error, code}` where error is the server's `message`, with 409 → "Responses are closed for this page." and 429 → "Too many answers — try again in a moment." when the server body has no message; `refresh` → results; unknown types ignored. Results are read and polled only when `access == Visible`: every 5000 ms (time provider), deduped on the body's `etag`, stopping on `closed` or a 403/404.
- [ ] **Step 1: Failing tests** (fake `IPageSource`, `FakeTimeProvider`, captured `postToPage`): a message with a wrong or missing nonce, or not JSON, → no post and no source call; ready → one init with `prior` = newest mine payload; `Hidden` access → ready/refresh/submit never call `ResultsAsync` and no timer is armed; `Visible` → results pushed after init and on ticks; submit OK → ack accepted + results pushed (Visible only); submit 409 without message → ack with the closed text and `code:409`; 429 likewise; unchanged etag on tick → no second results post; `closed:true` stops the poll; unknown type → no post; `PageHostDocumentTests`: srcdoc escapes `"` and `&`, contains the CSP meta with `connect-src 'none'`, has no `allow-same-origin`; fixture test asserts `PageBridge` handles exactly the inbound types listed in `bridge-v1.json`.
- [ ] **Step 2–4: Run FAIL, implement, run PASS.** Viewer name: the signed-in display name the app already shows (find its source; empty string if none).
- [ ] **Step 5: Manual check on macOS (and Windows if available)** with a hostile test page: it accepts a submit and re-opens pre-filled; a 300 KB post is dropped; a post from a nested frame is dropped; direct calls to the native channel from the page and from a nested frame are dropped by the nonce check (log line under a debug flag only); `window.open` and top-level navigation land in the browser.
- [ ] **Step 6: Commit** `Answer interactive pages from the in-app viewer (#2188)`

### Task 7: Entry points and live refresh

**Files:**
- Create: `src/Capacitor.App/Services/PageLinkRouter.cs`
- Modify: `ArtefactsTabViewModel.cs` / `ArtefactsView.axaml` (selecting a page shows `PageViewer` in the right pane instead of the document reader); every link entry point: `ChatTabViewModel.OpenLinkCommand` (`ChatTabViewModel.cs:~1003`), `WorkspaceViewModel.OpenCard` (`:~165`), `RemoteSessionViewModel.OpenCard` (`:~110`) — each asks the router first, else `LinkPolicy.Open`; `PlanActivity.PagePublished` → reload the open viewer when its id's `LatestVersion` grew
- Test: `ArtefactsTabViewModelTests.cs`, `PageLinkRouterTests.cs`, `ChatTabViewModelTests.cs`, `WorkspaceViewModelTests.cs`, `RemoteSessionViewModelTests.cs`

**Interfaces:**
- Consumes `PageViewerViewModel` (Task 5), `PageReference.ParseId` (exists, `src/Capacitor.Cli.Core/Pages/PageReference.cs`; it does not check hosts).
- Produces: `PageLinkRouter(Func<string?> profileServerUrl)` with `string? PageIdFor(string url)` — returns an id only when the link's scheme, host and port equal the profile server URL's (case-insensitive host, default ports normalized) and `PageReference.ParseId` finds one; otherwise null. `ArtefactsTabViewModel.OpenPage(string artefactId, string browserUrl)` selects the matching row if listed, else opens the viewer for that id, and raises `OpenRequested`.

- [ ] **Step 1: Failing tests**: selecting a page row sets `Reader == null` and `PageViewer.ArtefactId == row.ArtefactId`; a pages refresh with a higher `LatestVersion` for the open page reloads it (second content read); router: `https://srv.test/pages/abc` with profile `https://srv.test` → `abc`; `https://SRV.test:443/pages/abc` → `abc`; `https://evil.test/pages/abc`, `http://srv.test/pages/abc`, `https://srv.test.evil/pages/abc` → null; chat message link and a tool card's page link open the viewer in both a local workspace and a remote session; a foreign-host link opens the browser; `NoWebView` → both a row and a chat link open `browserUrl` in the browser with no content read.
- [ ] **Step 2–4: Run FAIL, implement, run PASS.**
- [ ] **Step 5: Commit** `Open page links in the in-app viewer (#2188)`

### Task 8: Docs, publish check, PR

- [ ] README desktop section: pages render in-app; Linux needs WebKitGTK, Windows the WebView2 runtime, else they open in the browser.
- [ ] `dotnet publish` both projects; grep IL warnings — none new.
- [ ] Full `Capacitor.App.Tests.Unit` and `Capacitor.Cli.Core.Tests.Unit` green (one suite at a time).
- [ ] Commit `Document in-app pages (#2188)`; open PR 2 based on PR 1 (`Closes #2188 — AI-4825`).
