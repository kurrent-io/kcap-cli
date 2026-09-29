# Work-context PR / issue section shells

Refines the desktop work-context pane after the chrome overhaul (AI-2956 / #983)
and the PR-card restore (AI-2988 / #1039). Parent surface: AI-2198.

## Problem

The chrome pass put the pane on a shared section grammar — eyebrow header, optional
meta, left-hairline body — but replaced the pull-request card with a title line that
only opened the reader tab. Checks and review state left the sidebar; a session with
more than one linked PR lost the picker. That piece was restored as the raised
`PullRequestCard`, which still fights the rest of the pane: Fluent ComboBox chrome,
and a separate issue `LinkCard` that lays key and title on one horizontal row and
overflows. Who's on it sits at the bottom with Session / Subagents, though its data
is about the work item.

## Decisions

Settled with the owner during brainstorming, 2026-09-21:

1. **Section shells, keep card guts.** Pull request and issue adopt the pane's
   section shell (eyebrow + meta + left-hairline body). The PR body keeps the
   restored card's content: lifecycle badge, multi-PR picker, repository, title,
   checks and review rows (open the matching reader section), notices, View PR and
   provider open. Rejected: title-only section (the #983 miss Alexey reverted);
   status-as-meta-only (same failure mode); keeping the raised bordered card for
   both PR and issue (fights the rest of the pane).
2. **Keep the multi-PR picker.** When there is more than one linked PR, one card
   body and a `kcapField` ComboBox. The reader tab already follows the selection.
   Two live PRs across repos is the usual case. Rejected: list every PR with full
   status rows; collapse merged ones into one-liners (extra rules for little gain
   when both are often open).
3. **Selection rules stay.** Explicit user pick is sticky; otherwise unique match
   on primary repo + current branch; otherwise first in the list. No preference for
   non-merged: `PullRequestLinkDto` carries no lifecycle, and auto-jumping after
   overview would fight an intentional pick of a merged PR.
4. **Who's on it under the work item.** Order: work item → Who's on it → pull
   request → separate issue → subagents → session. People are work-item data, not
   session data. Rejected for this pass: hide Who's on it until a work-item tab
   (Alexey's later option; AI-3037 asks for placement, not removal).

## Experience

### Section order

1. Work item (unchanged)
2. Who's on it (moved; same people UI — names, overflow chevron, requester fallback)
3. Pull request
4. Separate issue card (when `HasSeparateIssue`)
5. Subagents
6. Session

### Pull request

Eyebrow `PULL REQUEST`. Meta is the selected number label when present (e.g.
`#42`), else hidden. Body sits in the pane's `sectionBody` hairline.

Body content (from today's `PullRequestCard`, re-homed):

- Lifecycle status label when the overview can display
- ComboBox when `HasMultipleChoices`, class `kcapField`, same `Choices` /
  `Selected` binding; item label unchanged
- Repository label, title (wrap, max three lines)
- Checks and review status rows — same commands into the reader
- Stale / notice / reader-note copy and install / recheck actions as today
- View PR + provider open row; sign-in / link-GitHub when those states apply

Empty state stays the section empty note (`No pull request linked`) once both the
session list and the work-item read have settled. Legacy link-only cards remain
the fallback when there is no reader context.

### Issue

Eyebrow `ISSUE`. Meta is the issue key. Body is the title, wrapping, no horizontal
key+title pair. The section (or its title control) opens the URL when the policy
allows — same `OpenCommand` behaviour as today's `LinkCard`. No raised card border.

### Theme

The PR picker uses `ComboBox.kcapField` from `App.axaml` so the closed field and
popup match Settings and other Kcap fields. No new ComboBox style family.

## Out of scope

- A full work-item tab for people and issue detail
- Sorting or auto-selecting by merged / open
- Fetching overviews for every choice to decorate the picker
- Restyling Session or Subagents beyond reordering
- Changing how the reader tab binds to `Selected`

## Where to look

- `src/Capacitor.App/Views/WorkContextView.axaml` — section order, issue shell,
  PR section host
- `src/Capacitor.App/Views/PullRequestCard.axaml` — drop outer raised border when
  hosted in a section body, or inline the guts; apply `kcapField` on the selector
- `src/Capacitor.App/ViewModels/WorkContextViewModel.Projections.cs` — visibility
  helpers; issue presentation if the LinkCard template is retired for this slot
- Smoke / VM tests under `test/Capacitor.App.Tests.Unit/` that assert section
  presence, PR card binding, and people placement

## Verification

- Session with one PR: section shows lifecycle, checks, review; View PR opens the tab
- Session with two PRs: themed picker; switching updates card body and reader
- Issue with a long title: no horizontal overflow; key visible as meta
- Who's on it appears directly under the work item; Session is last
- Empty PR and legacy-link paths unchanged in meaning
