# AI-2834 — Support & feedback in the desktop app (design)

Linear: [AI-2834](https://linear.app/kurrent/issue/AI-2834). Mockups: https://claude.ai/artifact/WnkDQ44hobtnh2zUro8yUw (page "Desktop app"). Sibling: AI-2833 restores the web entry in kcap-server.

## Problem

The desktop app (`src/Capacitor.App`, Avalonia) has no way to reach support. Its Help menu offers Documentation and Changelog; the app menu offers About and Settings; the tray menu is about the daemon. The web app's Plain chat widget is a browser script, and this app embeds no web view, which is why AI-1820 deferred the desktop behind a spike that never ran.

The server side already exists. `POST /api/feedback` files a bug or feedback report as a Plain thread through the auth proxy, and `Capacitor.Cli.Core` already carries the client for it: `IFeedbackApi` / `FeedbackApi`, registered by `AddCapacitorHttp`, which is exactly the container `App.ServerHttp` builds once a profile resolves a server URL. `kcap feedback` is the lane's only caller today.

## Current state (what the change builds on)

- `AppMenuBar` builds the Window and Help menus for every window from a `Window.WindowOpenedEvent` class handler installed before `StartAsync`, once per window, and re-adopts them in AppKit on activation. It keeps no reference to the items it built. Help holds "Kurrent Capacitor Documentation" and "Changelog", each opening a URL through `LinkPolicy.Open(IUrlOpener, url)`. `NativeMenu` is exported on macOS only; on Windows and Linux the app draws custom chrome and no menu bar.
- `AppMenu` is the macOS application menu (About, Settings…); it retains its Settings item and flips `IsEnabled` in `SetSettingsAction`. Settings is enabled when the profile resolution carries both a `ProfileName` and a `ServerUrl`, because its store is keyed by the profile.
- `App.ServerHttp(profiles)` builds the authenticated lane container on `Resolution.ServerUrl` alone — a URL override with no profile name still yields a client. The main window is shown before `ConfigureSettingsMenu` runs.
- `App.OpenSettings` is the single-instance window pattern: activate the open window (restoring it from minimised) if any, else build the view model, wrap it in the window, hold the reference in a field, cancel `Closing` while the view model is busy unless shutdown has started, dispose the view model on `Closed`; `DisposeAndShutdownAsync` closes the window on quit.
- `SessionRailView`'s footer is one `DockPanel`: the connection dot, the connection word, the tenant name on the left; `Rail.HostedText` on the right. The whole strip carries a tooltip with the daemon identity. `MainWindow`'s Activity button shows the flyout pattern (`Button.Flyout` with `FlyoutPresenterClasses="kcapPanel"`).
- `FeedbackApi.SubmitAsync(category, message)` mints a fresh `ClientRequestId` per call, passes the category string through unchanged, and fixes the context to `source: "cli"`, `client_version: CapacitorVersion.CurrentDisplay()`, `os: RuntimeInformation.OSDescription`. It maps every refusal the server distinguishes to a `FeedbackResult` case and throws `CapacitorApiException` for anything else, including a 401. `Commands/FeedbackSubmission.cs` holds the three wire records in one file.
- `FeedbackCommand.ReportResultAsync` holds the user-facing sentence for each `FeedbackResult` case; its success line is pinned by tests as `✓ Sent to Kurrent support as {email} — replies will reach you by email.`.
- Server rules, taken as given: category exactly `bug` or `feedback`; the message is trimmed, then must be 1–8000 characters; `context.client_version` ≤ 100 and `context.os` ≤ 200 characters; `context.source` is carried as free text. The idempotency store is keyed by `(user, client_request_id)` only, replays a stored success for a while and never compares the payload; it evicts every non-success; Plain's `externalId` is audit-only, so a retry after an ambiguous failure can file again.
- `HomeViewModel.SignInExpiredNotice` is the app's wording for a lapsed sign-in.

## Decisions

### D1 — The desktop uses the feedback lane, not the widget

No web view is added. Support from the desktop is a native "Send feedback" window that posts through `IFeedbackApi`. The report lands in the same Plain workspace, with the same labels and the same reply-by-email path, as a report from the CLI or the web dialog.

### D2 — Three entry points

1. **Help menu** (macOS menu bar): after the existing two items and a separator, "Report a Bug…" and "Send Feedback…". Both open the same window; the first preselects Bug, the second Feedback.
2. **Rail footer help button**: a small icon button at the right end of the footer strip, before `HostedText`, opening a `kcapPanel` flyout upward with "Documentation", "Report a bug…", "Send feedback…". The button and Documentation are always enabled — reaching the docs needs no server, and a user with no server resolved is the one most likely to need them. Only the two report items follow D3. On Windows and Linux this flyout is the only entry, since those builds have no menu bar; on macOS it is the same items one click closer.
3. The tray menu is not touched. It is the daemon's surface, and a report window is not a daemon action.

Documentation in the flyout opens `AppMenuBar.DocsUrl` through `LinkPolicy.Open`, the same as the menu item.

### D3 — The report entries follow the server, and the menu keeps its copies current

The window needs a resolved server to post to. The two Help items and the two report items in the flyout are enabled exactly when `App.ServerHttp(profiles)` yields a client — a resolved `ServerUrl`, with or without a profile name, which includes `KCAP_URL` and command-line overrides. This is deliberately a wider condition than Settings uses: Settings needs a profile name for its store; a report needs only a server and the token store.

The oracle is evaluated once, at startup, after the profile resolves and **before** `_coordinator.ShowMainWindow()`, and the resulting `Action<FeedbackCategory>?` is handed to both `AppMenuBar` and the main window's view model at that point. `AppMenuBar` retains the two report items of every menu copy it attaches (a per-window list, the way `AppMenu` retains its Settings item) and flips their `IsEnabled` from `SetFeedbackAction`, so a copy attached to a window that opens later reads the current action, and a copy attached earlier is updated in place rather than rebuilt (rebuilding an exported item drops the AppKit registration until the window next becomes key).

Whether the tenant has `Features:Feedback` on is not probed: the lane answers a bare 404/405 when the feature is off, and the window shows that as a sentence. Probing would cost a request on every launch for a state that changes only when an admin flips a flag.

### D4 — The window

`FeedbackWindow` + `FeedbackViewModel` (`ReactiveObject`, disposable), opened through `App.OpenFeedback(FeedbackCategory)` with the Settings pattern, made explicit:

- `_feedbackWindow` field. A second open while the window exists restores it from minimised, activates it, and switches the category to the one asked for **unless a send is in flight** (the frozen snapshot is not touched).
- `Closing` is cancelled while a send is in flight unless shutdown has started; `Closed` disposes the view model; `DisposeAndShutdownAsync` closes the window on quit; the view model's lifetime token is linked to the app's shutdown token, so a quit cancels the call.

**Form.**
- Category: `Bug` | `Feedback`, two chips, one selected. These are the only values the lane accepts on both the server and the proxy.
- Message: multi-line `kcapField`. The user's text is trimmed before anything else.
- Hint line under the message: "Attached automatically: desktop {app version} · daemon {name} {version} · {OS} · {n} characters left".
- Actions: Cancel (closes), Send.

**Composition.** One function, `FeedbackMessageComposer.Compose(userText, trailer)` (Cli.Core), returns the wire message: the trimmed user text, a blank line, and one trailer line, `Sent from Kurrent Capacitor Desktop {app version} · daemon {name} {version}`. The lane's context has no slot for the daemon identity and the server caps the context fields at 100/200 characters, so the trailer is the only place a support engineer can read which daemon the reporter was on; the hint line tells the reporter it is there. `CanSend` is true when the trimmed user text is non-empty, the **composed** message is at most 8000 characters, and no send is in flight; "{n} characters left" is 8000 minus the composed length. `client_version` and `os` ride the context as they do for the CLI.

**Snapshot and retry.** A `ClientRequestId` is minted when the window opens. Pressing Send freezes the form and binds the id to an immutable `FeedbackSubmission` snapshot (category, composed message, id, source); the call sends that snapshot. After a refusal or a failure the form unfreezes: a Send with the category and text unchanged re-sends the same snapshot and id; any edit to the text or the category — including a second entry-point click that switches the category — mints a new id. What the id buys is stable correlation, single-flight across concurrent submissions, and a replay of a success the server observed within its retention; it is not a durable guarantee. Two duplicates are accepted and named: a retry after an ambiguous failure (the server or Plain accepted, the response was lost) files again if the content changed; and a quit during the call loses the window-local id while the server may still finish the submission. After `Sent`, the form clears and a new id is minted for the next report.

**Outcomes.** One line under the actions, or replacing the form on success:
- `Sent(email)` → "Sent. Replies go to {email}." with Send Another and Close.
- Every refusal → the sentence `FeedbackResultMessages.ForRefusal(result)` returns (D5), which for `TemporarilyUnavailable` carries the retry-after suffix.
- `CapacitorApiException` whose status is 401 → `HomeViewModel.SignInExpiredNotice`.
- Any other exception → "Couldn't send the report: {message}"; the form stays editable.

### D5 — The lane's client takes a typed submission, and the refusal sentences move down

`IFeedbackApi.SubmitAsync(FeedbackSubmission submission, CancellationToken ct)` replaces the two-string overload. `FeedbackSubmission(FeedbackCategory Category, string Message, Guid ClientRequestId, FeedbackSource Source)` with `FeedbackCategory { Bug, Feedback }` and `FeedbackSource { Cli, Desktop }`, all in `Capacitor.Cli.Core`. `FeedbackApi` is the one place the enums become wire strings (`bug`/`feedback`, `cli`/`desktop`). `FeedbackCommand` maps `--bug`/`--feedback` to the enum and mints its own id; its requests are byte-for-byte what they are today.

`FeedbackResultMessages.ForRefusal(FeedbackResult)` (Cli.Core) returns the sentence for every non-`Sent` case and `null` for `Sent`. Core shares refusals only: each surface owns its success presentation, so the CLI's pinned success line is unchanged and the window's shorter line is its own. `FeedbackCommand` prints `ForRefusal` for refusals and keeps its success line and exit codes.

`source` is free text on the server; `desktop` joins `cli` and the widget's own value.

### D6 — What this deliberately does not do

- No embedded Plain widget, no web view, no conversation history.
- No "Question" category: adding it means widening the allowlist in `FeedbackService` and the proxy's `SupportEndpoints` first.
- No attachments, screenshots, or log bundles.
- No tray entry, no keyboard shortcut.
- No probe of the tenant's feature state; no persistence of a pending report across a quit.

## Component changes (implementation map)

**`src/Capacitor.Cli.Core/`**
- `Http/IFeedbackApi.cs`, `Http/FeedbackApi.cs` — the typed overload; enum-to-wire mapping.
- `Commands/FeedbackSubmission.cs` — one file, one record. The three wire records that share that file today split into `FeedbackSubmitRequest.cs`, `FeedbackSubmitContext.cs`, `FeedbackSubmitResponse.cs` as part of touching the area.
- `Commands/FeedbackCategory.cs`, `Commands/FeedbackSource.cs` — the enums.
- `Http/FeedbackResultMessages.cs` — the shared refusal sentences.
- `Commands/FeedbackMessageComposer.cs` — the composition function.

**`src/Capacitor.Cli/Commands/FeedbackCommand.cs`** — builds a `FeedbackSubmission`; prints `ForRefusal` for refusals; success line and exit codes unchanged.

**`src/Capacitor.App/`**
- `Views/AppMenuBar.cs` — the two Help items; retained per window; `SetFeedbackAction(Action<FeedbackCategory>?)`.
- `Views/SessionRailView.axaml` — the footer help button and its flyout; `MainWindowViewModel` gains `OpenDocsCommand`, `OpenFeedbackCommand(FeedbackCategory)` and `CanOpenFeedback`.
- `Views/FeedbackWindow.axaml(.cs)` — the window, Settings chrome.
- `ViewModels/FeedbackViewModel.cs` — the form, the snapshot, the send, the outcome mapping.
- `App.axaml.cs` — `OpenFeedback(FeedbackCategory)` single-instance open; resolves `IFeedbackApi` from the `ServerHttp` container; evaluates the oracle and hands the action to the menu bar and the main window before the window is shown; closes the window on shutdown.

**Docs** — `docs/CHANGES.md` entry; README's desktop section names the entry; this spec.

## Delivery

One kcap-cli PR. No server change is required: the lane accepts the request as it stands. The kcap-server sibling (AI-2833) is independent.

## Behavior notes and accepted limitations

- A report sent against a server older than the feedback lane gets `NotConfigured` (bare 404), which reads as "not enabled". True enough for the reporter, and the admin's remediation is the same.
- `NoEmailOnFile` on a GitHubApp tenant is AI-1953's condition; the sentence sends the reporter to the web app, which is where the profile refreshes.
- The trailer makes the message the reporter typed and the message support reads differ by one line, and takes its length out of the 8000-character allowance. The hint line discloses both.
- The two duplicate cases in D4 are accepted rather than solved: solving them needs a durable pending-report store and a payload-aware server cache, neither of which this change adds.

## Testing

- **`AppMenuBarTests`**: the Help layout becomes `Kurrent Capacitor Documentation|Changelog|-|Report a Bug…|Send Feedback…`; each new item invokes the action with its category; with no action both are disabled; `SetFeedbackAction` after `Attach` flips the items of an already-attached window without rebuilding the menu; a window attached after the action is set gets enabled items.
- **`FeedbackViewModelTests`** (new): `CanSend` false on empty and whitespace-only text, false when the composed message is 8001 characters and true at exactly 8000 (boundary against the largest accepted user text), false while busy; the composed message carries the trailer once with the daemon name and version; `Bug` and `Feedback` reach the fake api as their enum values; the same snapshot and id are sent twice when the content is unchanged after `TemporarilyUnavailable`; a new id after an edit to the text, after a category switch, and after `Sent`; a lost-success (thrown) response followed by an edit sends a new id; every `FeedbackResult` refusal and the 401 exception map to their sentence; a non-401 exception leaves `CanSend` true.
- **`FeedbackApiTests`** (Cli.Core): `Bug`/`Feedback` and `Cli`/`Desktop` serialise to their lowercase wire strings; `client_request_id` passes through unchanged.
- **`FeedbackMessageComposerTests`**: trim, separator and trailer; length arithmetic at the boundary.
- **`FeedbackResultMessagesTests`**: one assertion per refusal case; `Sent` returns null.
- **`FeedbackCommandTests`** (existing): the success line and exit codes are unchanged; `--bug` sends `bug`.
- **App composition** (`AppStartupTests` / `DesktopWindowLifecycleTests` patterns): a URL-only resolution enables the report entries; the first main window's Help items are enabled after startup; second open activates and switches the category; the switch is refused while a send is in flight; close is refused while busy and allowed on shutdown; the shutdown token cancels an in-flight send and disposes the view model.
- **`FeedbackWindowSmokeTests`** (headless, the `SettingsWindowSmokeTests` pattern): the selected chip reflects the category; Send follows `CanSend`; `Sent` swaps the form for the confirmation.
- **`MainWindowSmokeTests`**: the rail footer renders the help button; Documentation is enabled with no server; the report items enable once the action is set.

## Out of scope

The web app (AI-2833), the auth proxy, the Plain widget, and a "Question" category.
