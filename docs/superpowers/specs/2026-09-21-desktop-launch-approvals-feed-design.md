# Desktop launch-approvals feed (AI-3061)

Linear: [AI-3061](https://linear.app/kurrent/issue/AI-3061/desktop-clarify-activity-as-launch-approvals-feed).
Builds on the consent decision log and Activity surface from AI-1652 / AI-1830; placement as a
launcher flyout from AI-2199.

## 1. Problem

The launcher chip labeled **Activity** opens the local consent decision log
(`consent-decisions.jsonl`). That log is useful — it answers “what did this machine allow or deny
launching?” — but the surface does not say so:

- **Activity** reads as session or agent noise, not launch consent history.
- A seven-column spreadsheet (`Time`, `Outcome`, `Requester`, `Kind`, `Repo`, `Vendor`, `Source`)
  looks like admin tooling; labels like `Source: owner` / `default policy` need product knowledge.
- Outcomes render as raw `allowed` / `denied`.
- Requester emails use end-of-string ellipsis in a narrow column, so the domain is often lost.
- The empty state (“Allow and deny choices from consent prompts show up here.”) is clearer than the
  filled table or the chip label.

## 2. Decisions

| Decision | Choice |
|---|---|
| Product scope | **Consent history only.** No broader “session Activity” feed. |
| Placement | Keep the top-right **chip + flyout** on the launcher pane (same visibility / polling gate). Do not move into Settings. |
| Chip label | **Launches** |
| Flyout title | **Launch approvals** |
| Layout | Compact **feed rows**, not a column table. No column headers. |
| Chip badge | **Out of scope** for this change (no count pip). |

## 3. Naming and empty state

- Chip text: `Launches` (control may keep an `ActivityButton` / `Activity*` automation name or be
  renamed with smoke-test updates — either is fine as long as tests still find the chip and flyout).
- Flyout header: `Launch approvals`.
- Empty primary: `No launches decided yet`.
- Empty secondary (unchanged intent): `Allow and deny choices from consent prompts show up here.`

## 4. Row layout

Each `ConsentDecisionRecord` projects to one feed row with three lines:

**Primary** (body text weight)

- Outcome: `Allowed` or `Denied` (green / red via the existing outcome brush tokens).
- Vendor as today (`claude`, `codex`, … — no case change required beyond current display).
- Kind label from `ConsentPromptViewModel.KindLabelOf`, **omitted when the kind is `agent`**
  (plain Agent is noise; Review / Review flow stay).

Example primary: `Allowed · Claude · Review flow` or `Denied · Codex`.

**Secondary** (muted)

- Requester display string (see §5), full value in tooltip.
- Repo leaf (`RepoLabel.Leaf`), full path in tooltip.
- Source label from `ActivityViewModel.SourceLabelOf`, **omitted when the label is `you`**.

Example secondary: `alex…@company.com · kcap-cli · rule`.

**Tertiary** (faint)

- Local time as today: `MMM d HH:mm`; tooltip full `yyyy-MM-dd HH:mm:ss`. Unparseable `decided_at`
  still renders verbatim in both.

**Flyout chrome**

- Width about **420–480px** (narrower than today’s 720px table).
- `MaxHeight` ~420, vertical scroll; horizontal scroll off unless content truly overflows.
- Soft row separators; `kcapPanel` flyout presenter unchanged.
- Title row at the top of the panel when non-empty; no spreadsheet header grid.

## 5. Requester truncation

Project a display string on the row; keep the full requester for the tooltip.

- If the string contains `@` (email-shaped): truncate the **local part** and keep `@` + domain.
  Example: `very.long.local-part@company.com` → `very.long.…@company.com` (exact head length is an
  implementation choice that fits the secondary line; domain stays intact unless the domain alone
  exceeds the secondary line budget — then middle-truncate the whole string).
- Otherwise: middle-truncate with the same spirit as `WorkContextViewModel.MiddleTruncate`
  (head + `…` + tail), full string in tooltip.
- Fallback order unchanged: `requester_display ?? requester ?? "unknown"`.

## 6. Unchanged contracts

These stay as AI-1652 / current code:

- Reader: `ConsentDecisionLogReader` / injected `Func<ConsentLogReadResult>` over
  `consent-decisions.jsonl` (+ `.1`); works with the daemon stopped.
- Refresh: flyout open (visibility true), 2-tick stat poll while visible, own-resolution nudge via
  `RequestRefresh`.
- Complete / incomplete display rule; 200-record tail; newest first.
- Source label mapping (`owner`, `rule[…]` → `rule`, `default` → `default policy`, `prompt_user` →
  `you`, etc.).

## 7. Out of scope

- Broader activity (session starts/stops, needs-you, chat events).
- Badge or unread count on the chip.
- Moving the surface into Settings or the Help menu.
- Daemon / IPC / log format changes.
- Changing consent prompt window copy or behavior.

## 8. Testing

- Unit: row projection — outcome title case, kind omission for `agent`, source omission for `you`,
  email middle-truncate keeps domain, non-email middle-truncate, time formatting unchanged.
- Existing Activity refresh / Complete / single-flight tests stay green (rename only if control names
  change).
- Smoke: chip opens the flyout; empty-state strings visible when the log is empty.

## 9. Files (expected)

- `src/Capacitor.App/Views/MainWindow.axaml` (+ code-behind only if names change)
- `src/Capacitor.App/ViewModels/ActivityViewModel.cs` (row shape / projection helpers)
- `test/Capacitor.App.Tests.Unit/ActivityViewModelTests.cs`
- Smoke tests that assert `ActivityButton` / empty text, if renamed
