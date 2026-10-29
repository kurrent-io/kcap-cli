# AI-3051 — Desktop chat bubble chrome (design)

GitHub: [#1083](https://github.com/kurrent-io/kcap-cli/issues/1083). Linear: [AI-3051](https://linear.app/kurrent/issue/AI-3051).

## Problem

Tool-group cards (Command, Skill, and the other kind-chip categories) read as centered in the desktop chat. Avalonia centers a child that is `HorizontalAlignment="Stretch"` and capped by `MaxWidth` when the list column is wider than the cap. Notes and assistant prose already use `Left`; user turns use `Right`. Kind labels are text-only `toolKindChip` blocks duplicated across templates, with inconsistent padding, fill, and whether a kind chip appears at all (lone tool vs multi-call summary). The web chat paints a Material icon beside each tool kind via `EventStyles.GetToolIcon`.

Alexey asked for left-aligned system bubbles, icons like the web UI, user speech still on the right, and questions allowed to stay centered. Similar bubbles should look and behave alike without inventing a new transcript model.

## Current state

Chat rows are distinct view-model types matched by DataTemplates in `ChatTabView.axaml` and `PendingCardTemplates.axaml`:

| Row | Shell today | Header today |
|---|---|---|
| `UserTurnItem` | Right, MaxWidth 520, raised fill, no border | `You` chip |
| `AssistantTextItem` | Left, MaxWidth 660, no card | Vendor title chip |
| `SystemNoteItem` | Left, MaxWidth 660, surface + border | `Note` chip |
| `ToolGroupItem` | Stretch + MaxWidth 660 (appears centered), MinWidth 320, surface + border | Lone: kind chip + status; multi: summary button only |
| `PendingCardItem` | Stretch + MaxWidth 660 host | Permission / Question / step header chips inside raised cards |

`ToolSummary` already owns categories and `ChipLabel`. Status pills stay on tool rows / lone headers. Pending permission and question cards are interactive "needs you" prompts, not system narration.

## Decisions

### D1 — Roles and placement

- **User speech** — right-aligned raised bubble; label `You`; no category icon.
- **Assistant prose** — left markdown column with vendor title chip; not forced into a bordered system card.
- **System cards** — Note and tool groups: `HorizontalAlignment="Left"`, MaxWidth 660, shared system shell. Never `Stretch` with MaxWidth (that is the centering trap).
- **Prompt cards** — Permission, AskUserQuestion, ACP question: centered host, MaxWidth 660, shared prompt shell.

### D2 — Approach: shared styles + thin helpers, keep templates

Do not collapse row VMs into one "system bubble" type, and do not wrap every body in a single generic `SystemCard` control this pass. Extract:

1. Style classes `chatSystemCard` and `chatPromptCard` (alignment, max width, padding, corner radius, fill, border).
2. `ChatKindHeader` UserControl: icon + label, optional trailing status content presenter.
3. `ToolCategoryIcons` for categories, plus fixed-label paths for `Note` / `Permission` / `Question` when those headers use `ChatKindHeader`. `You` and the assistant title stay text-only (no icon).

Templates keep their bodies (markdown, tool rows, option lists). Shell + header go through the shared bits so chrome cannot drift per template.

### D3 — Visual grammar

**System card (`chatSystemCard`)**

- Left, MaxWidth 660, no MinWidth floor (drop the tool-group-only 320 so notes and commands size the same way).
- Padding `14,10`, CornerRadius 10, `KcapSurfaceBrush`, 1px `KcapBorderBrush`.
- Header: `ChatKindHeader` with muted icon + label.

**Prompt card (`chatPromptCard`)**

- Center via the pending `ContentControl` host (`HorizontalAlignment="Center"`), MaxWidth 660.
- Same padding/radius/border as system; fill `KcapSurfaceRaisedBrush` (prompts stay slightly raised vs system).
- Header: `ChatKindHeader` with icon + `Permission` / `Question` (or the step header text when present).

**User bubble** — keep current right/raised treatment; optional reuse of header typography only, no category icon.

### D4 — Icons

Material-*like* stroked Path geometries at ~14×14, keyed by `ToolCategory`, mirroring the web vocabulary at category grain (not per vendor tool name):

| Category | Web analogue |
|---|---|
| Command | Terminal |
| Skill | AutoAwesome |
| Read | Description |
| Edit | EditNote |
| Search | Search |
| WebSearch | TravelExplore |
| Fetch | Language |
| Agent | GroupWork |
| Plan | Checklist |
| Question | QuestionAnswer |
| Other | Build |

Fixed labels (`Note`, `Permission`, `Question`) get dedicated paths in the same map. Icons use muted foreground (not web MudBlazor colour tints). Pixel-perfect MudBlazor SVG copies are out of scope.

### D5 — Label rules

- **Lone tool card** — icon + `ToolSummary.ChipLabel(category)` + existing status slot.
- **Multi-call group** — keep the expandable summary line (`Ran a command · …`); prefix the dominant/first settled category's icon on that header so the card still reads as a kinded system card. No second kind-chip row.
- **Note / Permission / Question** — always icon + fixed label (ACP question always `Question`; multi-step AskUserQuestion keeps step `Header` when shown, still through `ChatKindHeader`).
- **User / Assistant** — text label only.

Dominant category for a multi-call summary: first category in arrival order among settled calls (same order `ToolSummary.Describe` already walks). Expose as a bindable property on `ToolGroupItem` if the view needs it.

### D6 — Consistency fixes in scope

Unify the drifts the audit found: alignment, drop tool-group MinWidth, shared padding, system vs prompt fill split above, every card-ish system/prompt row goes through `ChatKindHeader`. Do not rewrite assistant into a card. Do not merge consecutive system events into one transcript row.

## Touch points

- `src/Capacitor.App/Views/ChatTabView.axaml` — shells, Left on tool groups, wire header.
- `src/Capacitor.App/Views/PendingCardTemplates.axaml` — prompt shells + header.
- `src/Capacitor.App/App.axaml` or a dedicated `ChatBubbleStyles.axaml` — `chatSystemCard` / `chatPromptCard` / header styles.
- New: `ChatKindHeader.axaml(.cs)`, `ToolCategoryIcons.cs`.
- `ToolGroupItem` — dominant/header category (+ icon) for multi-call summary if needed.
- `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs` — alignment + header presence.
- Unit tests for icon map completeness over every `ToolCategory`.

## Tests

- Headless: a tool-group Border is `HorizontalAlignment.Left`; a pending-card host is centered (or its card Border is).
- Headless: a lone Command (and optionally Skill) row exposes a `ChatKindHeader` with non-empty icon geometry and the expected label.
- Unit: `ToolCategoryIcons` returns a path for every `ToolCategory` value.

## Out of scope

- Collapsing all system events into one bubble in the transcript model.
- Exact MudBlazor Material paths or per-tool colour tints from the web UI.
- Bordered card chrome around assistant prose.
- README / CLI help (desktop chrome only).

## Success

A screenshot for Alexey shows Command/Skill/Note cards left-aligned with matching icon+label headers; user still right; questions/permissions still centered; no Stretch+MaxWidth centering on system cards.
