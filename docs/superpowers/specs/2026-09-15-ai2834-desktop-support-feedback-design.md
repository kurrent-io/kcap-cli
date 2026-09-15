# AI-2834 — Support & feedback in the desktop app (design)

Linear: [AI-2834](https://linear.app/kurrent/issue/AI-2834). Mockups: https://claude.ai/artifact/WnkDQ44hobtnh2zUro8yUw (page "Desktop app"). Sibling: AI-2833 restores the web entry in kcap-server.

## Problem

The desktop app (`src/Capacitor.App`, Avalonia) has no way to reach support. Its Help menu offers Documentation and Changelog; the app menu offers About and Settings; the tray menu is about the daemon. The web app's Plain chat widget is a browser script, and this app embeds no web view, which is why AI-1820 deferred the desktop behind a spike that never ran.

The server side already exists. `POST /api/feedback` files a bug or feedback report as a Plain thread through the auth proxy, and `Capacitor.Cli.Core` already carries the client for it: `IFeedbackApi` / `FeedbackApi`, registered by `AddCapacitorHttp`, which is exactly the container `App.ServerHttp` builds once a profile names a server. `kcap feedback` is the lane's only caller today.

## Current state (what the change builds on)

- `AppMenuBar` builds the Window and Help menus for every window (`Window.WindowOpenedEvent` class handler) and re-adopts them in AppKit on activation. Help holds "Kurrent Capacitor Documentation" and "Changelog", each opening a URL through `LinkPolicy.Open(IUrlOpener, url)`. `NativeMenu` is exported on macOS only; on Windows and Linux the app draws custom chrome and no menu bar.
- `AppMenu` is the macOS application menu (About, Settings…); Settings is enabled through `SetSettingsAction` once a profile resolves a server, and disabled otherwise.
- `App.OpenSettings` is the single-instance window pattern: activate the open window if any, else build the view model, wrap it in the window, hold the reference, dispose the view model on `Closed`, refuse to close while busy.
- `SessionRailView`'s footer is one `DockPanel`: the connection dot, the connection word, the tenant name on the left; `Rail.HostedText` on the right. The whole strip carries a tooltip with the daemon identity.
- `FeedbackApi.SubmitAsync(category, message)` mints a fresh `ClientRequestId` per call and fixes the context to `source: "cli"`, `client_version: CapacitorVersion.CurrentDisplay()`, `os: RuntimeInformation.OSDescription`. It maps every refusal the server distinguishes to a `FeedbackResult` case and throws `CapacitorApiException` for anything else, including a 401.
- `FeedbackCommand.ReportResultAsync` holds the user-facing sentence for each `FeedbackResult` case.
- `HomeViewModel.SignInExpiredNotice` is the app's wording for a lapsed sign-in.

## Decisions

### D1 — The desktop uses the feedback lane, not the widget

No web view is added. Support from the desktop is a native "Send feedback" window that posts through `IFeedbackApi`. The report lands in the same Plain workspace, with the same labels and the same reply-by-email path, as a report from the CLI or the web dialog.

### D2 — Three entry points

1. **Help menu** (macOS menu bar): after the existing two items and a separator, "Report a Bug…" and "Send Feedback…". Both open the same window; the first preselects Bug, the second Feedback.
2. **Rail footer help button**: a small icon button at the right end of the footer strip, before `HostedText`, opening a `kcapPanel` flyout upward with "Documentation", "Report a bug…", "Send feedback…". On Windows and Linux this is the only entry, since those builds have no menu bar; on macOS it is the same three items one click closer.
3. The tray menu is not touched. It is the daemon's surface, and a report window is not a daemon action.

Documentation in the flyout opens `AppMenuBar.DocsUrl` through `LinkPolicy.Open`, the same as the menu item.

### D3 — The entries follow the server, not the tenant's flag

The window needs a resolved server to post to. The Help items and the footer button are enabled exactly when `App.ServerHttp(profiles)` yields a client — the condition `SetSettingsAction` already gates Settings on — and disabled otherwise, so a not-yet-signed-up app shows the items greyed rather than opening a window that cannot send.

Whether the tenant has `Features:Feedback` on is not probed: the lane answers a bare 404/405 when the feature is off, and the window shows that as a sentence. Probing would cost a request on every launch for a state that changes only when an admin flips a flag.

### D4 — The window

`FeedbackWindow` + `FeedbackViewModel` (`ReactiveObject`, disposable), opened through `App.OpenFeedback(category)` with the Settings single-instance pattern. A second open while the window exists activates it and switches the category if the caller asked for one.

**Form.**
- Category: `Bug` | `Feedback`, two chips, one selected (the mockup). These are the only values the lane accepts on both the server and the proxy.
- Message: multi-line `kcapField`, required; `CanSend` is false when the trimmed text is empty or over 8000 characters (the server's cap), or while a send is in flight.
- Hint line under the message: "Attached automatically: desktop {app version} · daemon {name} {version} · {OS}".
- Actions: Cancel (closes), Send.

**What is sent.** `FeedbackSubmission(Category, Message, ClientRequestId, Source: "desktop")`, where `Message` is the user's text followed by a blank line and one trailer line, `Sent from Kurrent Capacitor Desktop {app version} · daemon {name} {version}`. The lane's context has no slot for the daemon identity and the server caps the context fields at 100/200 characters, so the trailer is the only place a support engineer can read which daemon the reporter was on. The hint line tells the reporter it is there. `client_version` and `os` ride the context as they do for the CLI.

**Idempotency.** `ClientRequestId` is minted when the window opens and kept until a report is accepted; a Send after a retryable failure reuses it, so a retry cannot file twice. After `Sent`, the form clears and a new id is minted for the next report.

**Outcomes.** One line under the actions, or replacing the form on success:
- `Sent(email)` → "Sent. Replies go to {email}." with Send Another and Close.
- `NotConfigured` → "This server doesn't have support intake enabled."
- `Unavailable` → "Support intake isn't configured on this server — ask your admin."
- `NoEmailOnFile` → "Your account has no email on file — sign in to the web app once, then retry."
- `RateLimited` → "You've sent several reports recently — try again in a few minutes."
- `TemporarilyUnavailable(retryAfter)` → "Couldn't reach Kurrent support (temporary) — try again in {n}s." (or without the suffix).
- `Invalid(message)` → the server's message.
- `CapacitorApiException` with a 401 → `HomeViewModel.SignInExpiredNotice`.
- Any other exception → "Couldn't send the report: {message}"; the form stays editable.

Closing is refused while a send is in flight, as Settings refuses while busy; the view model's lifetime token is linked to the app's shutdown token so a quit cancels the call.

### D5 — The lane grows one overload, and the sentences move down

`IFeedbackApi` gains `SubmitAsync(FeedbackSubmission submission, CancellationToken ct)`, where `FeedbackSubmission(string Category, string Message, Guid ClientRequestId, string Source)`. The existing two-argument overload keeps its behaviour by delegating with a fresh id and `"cli"`, so `kcap feedback` is unchanged.

The per-case sentences leave `FeedbackCommand` for `FeedbackResultMessages.For(FeedbackResult)` in `Capacitor.Cli.Core`, and the command prints what it returns. One vocabulary for the CLI and the app; a future wording change lands once.

`source` is free text on the server (`FeedbackContextDto.Source` is carried, not validated); `"desktop"` joins `"cli"` and the widget's own value.

### D6 — What this deliberately does not do

- No embedded Plain widget, no web view, no conversation history.
- No "Question" category: adding it means widening the allowlist in `FeedbackService` and the proxy's `SupportEndpoints` first.
- No attachments, screenshots, or log bundles.
- No tray entry, no keyboard shortcut.
- No probe of the tenant's feature state.

## Component changes (implementation map)

**`src/Capacitor.Cli.Core/`**
- `Http/IFeedbackApi.cs`, `Http/FeedbackApi.cs` — the `FeedbackSubmission` overload; the two-argument one delegates.
- `Commands/FeedbackSubmission.cs` — the new `FeedbackSubmission` record beside the wire `FeedbackSubmitRequest`.
- `Http/FeedbackResultMessages.cs` — the shared sentences.

**`src/Capacitor.Cli/Commands/FeedbackCommand.cs`** — prints `FeedbackResultMessages.For(result)`; exit codes unchanged.

**`src/Capacitor.App/`**
- `Views/AppMenuBar.cs` — the two Help items; the constructor takes an `Action<FeedbackCategory>?`-returning accessor the way it takes `showMainWindow`, so the items enable with the server.
- `Views/SessionRailView.axaml` — the footer help button and its flyout; `MainWindowViewModel` exposes `OpenFeedbackCommand` / `OpenDocsCommand` and an `CanOpenFeedback` the button binds to.
- `Views/FeedbackWindow.axaml(.cs)` — the window, Settings chrome.
- `ViewModels/FeedbackViewModel.cs`, `ViewModels/FeedbackCategory.cs` — the form, the send, the outcome mapping.
- `App.axaml.cs` — `OpenFeedback(FeedbackCategory)` single-instance open; resolves `IFeedbackApi` from the `ServerHttp` container; wires the menu bar and the main window's commands beside `ConfigureSettingsMenu`.

**Docs** — `docs/CHANGES.md` entry; README's desktop section names the entry; this spec.

## Delivery

One kcap-cli PR. No server change is required: the lane accepts the request as it stands. The kcap-server sibling (AI-2833) is independent.

## Behavior notes and accepted limitations

- A report sent against a server older than the feedback lane gets `NotConfigured` (bare 404), which reads as "not enabled". True enough for the reporter, and the admin's remediation is the same.
- `NoEmailOnFile` on a GitHubApp tenant is AI-1953's condition; the sentence sends the reporter to the web app, which is where the profile refreshes.
- The trailer makes the message the reporter typed and the message support reads differ by one line. The hint line discloses it; a reporter who removes nothing gets exactly what the hint says.

## Testing

- **`AppMenuBarTests`**: the Help layout becomes `Kurrent Capacitor Documentation|Changelog|-|Report a Bug…|Send Feedback…`; each new item invokes the accessor's action with its category; with no action the two items are disabled.
- **`FeedbackViewModelTests`** (new): `CanSend` false on empty, whitespace-only and 8001-character messages and while busy; the same `ClientRequestId` reaches the fake api on two Sends around a `TemporarilyUnavailable`; a new id after `Sent`; the trailer is appended once with the daemon name and version; every `FeedbackResult` case and the 401 exception map to their sentence; a non-401 exception leaves `CanSend` true.
- **`FeedbackApiTests`** (Cli.Core, existing file if present, else new): the new overload passes `client_request_id` and `source` through unchanged; the two-argument overload still sends `source: "cli"`.
- **`FeedbackResultMessagesTests`**: one assertion per case, so a wording change is a deliberate diff.
- **`MainWindowSmokeTests`**: the rail footer renders the help button, disabled before a server resolves and enabled after.

## Out of scope

The web app (AI-2833), the auth proxy, the Plain widget, and a "Question" category.
