# Work-context PR / issue section shells Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restyle the work-context sidebar so pull request and issue use the pane's section shell (eyebrow + meta + left hairline) while keeping live PR status and a themed multi-PR picker, and place Who's on it under the work item.

**Architecture:** Visual-only reshape of `WorkContextView.axaml` and `PullRequestCard.axaml`. No selection, read, or visibility-predicate changes. Section order becomes work item → Who's on it → PR → issue → subagents → session. The PR card loses its raised outer border and duplicate "Pull request / #n" header (the section owns those); its ComboBox takes `kcapField`. Issue drops the horizontal LinkCard layout for the same section shell.

**Tech Stack:** Avalonia UI, existing `sectionHeader` / `sectionBody` / `kcapField` styles, TUnit + Avalonia headless smoke tests.

**Spec:** `docs/superpowers/specs/2026-09-21-work-context-pr-issue-section-shells-design.md` (AI-3039)

## Global Constraints

- Keep PR status in the sidebar (lifecycle, checks, review rows) — never title-only.
- Multi-PR: one themed ComboBox (`kcapField`); selection rules unchanged (no merge preference).
- Who's on it is work-item data: sit under the work item, before pull request.
- Issue: key as section meta, title wraps in the body; no horizontal key+title overflow.
- Do not invent a work-item tab, collapse-merged lists, or overview fetches for every choice.
- Green/orange/yellow remain status-only; section chrome uses muted/text/surface tokens.
- Comments: scarce; no ticket/history narration (CLAUDE.md).

## File map

| File | Role |
|------|------|
| `src/Capacitor.App/Views/WorkContextView.axaml` | Section order; PR/issue shells; Who's on it move |
| `src/Capacitor.App/Views/PullRequestCard.axaml` | Drop raised chrome + duplicate header; `kcapField` on picker |
| `test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs` | Order, issue layout, PR picker class, status rows |
| `test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs` | Named-control list if new names appear |
| `docs/CHANGES.md` | Short entry for the shape |

No ViewModel API changes expected. `ShowsPullRequestCard`, `HasSeparateIssue`, `OpenCommand`, and selection stay as they are.

---

### Task 1: Move Who's on it under the work item

**Files:**
- Modify: `src/Capacitor.App/Views/WorkContextView.axaml`
- Test: `test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs`
- Possibly: `test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs` (add `WhoSection` if named)

**Interfaces:**
- Consumes: existing `WhoToggle`, `ContributorList`, `RequesterRow`, `TogglePeopleCommand`
- Produces: `WhoSection` StackPanel (x:Name) as the Who's-on-it root; document order in the pane's outer `StackPanel`

- [ ] **Step 1: Write the failing order test**

In `WorkContextViewSmokeTests.cs`, add:

```csharp
/// Who's on it is work-item data: it sits under the work item, before pull request.
/// Session stays last (after subagents).
[Test]
[NotInParallel("AvaloniaSession")]
public async Task Section_order_is_work_item_then_people_then_pr_then_issue_then_subagents_then_session() {
    await RunOnUiAsync(async () => {
        await using var host = new Host();
        await host.ShowAsync(KeyOnlyRead());

        var pane = host.Find<ScrollViewer>("PaneScroll").Content as Panel
            ?? throw new InvalidOperationException("pane body");
        // Outer content StackPanel is the ScrollViewer Content's child StackPanel.
        var root = ((Border)host.Window.Content!).Child is ScrollViewer sv
            ? (StackPanel)sv.Content!
            : throw new InvalidOperationException("expected pane stack");
        // Prefer: walk from WhoToggle up to the named sections' common parent.
        var who = host.Find<StackPanel>("WhoSection");
        var pr = host.Find<StackPanel>("PullRequestSection");
        var issue = host.Find<Control>("IssueCard");
        var subagents = host.Find<StackPanel>("SubagentsSection");
        var session = host.Find<Button>("SessionToggle").FindAncestorOfType<StackPanel>()!;

        int Index(Control c) {
            var p = c;
            while (p.Parent is not null && !ReferenceEquals(p.Parent, who.Parent)) p = (Control)p.Parent!;
            return ((Panel)who.Parent!).Children.IndexOf(p);
        }

        // Simpler approach used in the suite: find the outer StackPanel Margin="16,16,16,16"
        var body = host.Find<ScrollViewer>("PaneScroll").Content as StackPanel
            ?? throw new InvalidOperationException("pane stack");
        int At(Control c) {
            Control? walk = c;
            while (walk is not null && walk.Parent != body) walk = walk.Parent as Control;
            return body.Children.IndexOf(walk!);
        }

        await Assert.That(At(who)).IsLessThan(At(pr));
        await Assert.That(At(pr)).IsLessThan(At(issue));
        // Subagents may be collapsed/hidden but still in the tree when HasSubagents is false —
        // only assert session after issue when subagents are absent; when present, after subagents.
        await Assert.That(At(issue)).IsLessThan(At(session));
        if (subagents.IsEffectivelyVisible)
            await Assert.That(At(subagents)).IsLessThan(At(session));
    });
}
```

Use the suite's real `Host` / `Find` patterns. If `PaneScroll.Content` is not the `StackPanel` directly, adjust to the actual tree (today: `Border` → `ScrollViewer` → `StackPanel`). Drop the unused `Index` helper; keep only `At`.

- [ ] **Step 2: Run the test — expect fail**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*WorkContextViewSmokeTests/*Section_order*"
```

Expected: FAIL because Who's on it is currently after Session/Subagents (`At(who) > At(pr)`).

- [ ] **Step 3: Reorder the XAML**

In `WorkContextView.axaml`:

1. Cut the entire Who's-on-it `StackPanel` (from the comment through its closing tag, currently near the end).
2. Paste it immediately after the work-item `Border` (the block ending just before `PullRequestSection`), still `Margin="0,18,0,0"`.
3. Wrap/name it: `x:Name="WhoSection"` on that StackPanel.
4. Leave Pull request, Issue, Session, Subagents in place for now — only Who moves. (Session before Subagents is wrong per AI-3037; fix order fully in this step:)

After the move, also swap Session and Subagents so the order is:

1. Work item Border  
2. `WhoSection`  
3. `PullRequestSection`  
4. `IssueCard`  
5. `SubagentsSection`  
6. Session StackPanel  

- [ ] **Step 4: Run the order test — expect pass**

Same command as Step 2. Expected: PASS.

Also run existing Who tests:

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*WorkContextViewSmokeTests/*Who*"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Views/WorkContextView.axaml test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs
git commit -m "$(cat <<'EOF'
[AI-3039] desktop: Put Who's on it under the work item

People are work-item data; Session stays last after subagents.
EOF
)"
```

If `WorkspaceViewSmokeTests` gains `"WhoSection"` in the name list, include that file.

---

### Task 2: PR section shell + themed picker

**Files:**
- Modify: `src/Capacitor.App/Views/WorkContextView.axaml` (PR section host)
- Modify: `src/Capacitor.App/Views/PullRequestCard.axaml`
- Test: `test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs`

**Interfaces:**
- Consumes: `ShowsPullRequestCard`, `PullRequests.NumberLabel`, `PullRequestCard` guts, `kcapField` in `App.axaml`
- Produces: PR eyebrow/meta outside the card; card without raised border; picker with `Classes="kcapField"`

- [ ] **Step 1: Extend the PR smoke test**

Update `The_pull_request_card_shows_its_picker_and_status_rows_in_the_pane` (or add a sibling test) to also assert:

```csharp
var header = host.Find<Button>("PullRequestHeader");
await Assert.That(header.IsEffectivelyVisible).IsTrue();
var meta = host.Find<TextBlock>("PullRequestNumberMeta");
await Assert.That(meta.Text).IsEqualTo(pullRequests.NumberLabel);
await Assert.That(picker.Classes.Contains("kcapField")).IsTrue();

// No raised outer card chrome — the section hairline is the frame
var outer = card.GetVisualDescendants().OfType<Border>().FirstOrDefault(b =>
    b.Parent == card || ReferenceEquals(b, card.Content));
// Prefer: card root content is a StackPanel, not a Border with CornerRadius 8 + raised fill
await Assert.That(card.Content).IsTypeOf<StackPanel>();
```

Keep existing assertions on picker count, checks/reviews visibility, and empty-state handoff.

- [ ] **Step 2: Run — expect fail**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*WorkContextViewSmokeTests/*pull_request_card*"
```

Expected: FAIL — no `PullRequestHeader` / no `kcapField`.

- [ ] **Step 3: Wrap the card in a section shell**

In `WorkContextView.axaml`, replace the bare `PullRequestCard` binding with:

```xml
<StackPanel IsVisible="{Binding ShowsPullRequestCard}">
    <Button x:Name="PullRequestHeader" Classes="sectionHeader">
        <Grid ColumnDefinitions="*,Auto" ColumnSpacing="10">
            <TextBlock Text="PULL REQUEST" Classes="eyebrow" />
            <TextBlock x:Name="PullRequestNumberMeta" Grid.Column="1"
                       Text="{Binding PullRequests.NumberLabel}" Classes="sectionMeta"
                       IsVisible="{Binding PullRequests.NumberLabel, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
        </Grid>
    </Button>
    <Border Classes="sectionBody">
        <views:PullRequestCard x:Name="PullRequestCard" DataContext="{Binding PullRequests}" />
    </Border>
</StackPanel>
```

Keep `LinkCards` and the empty-state stack as siblings inside `PullRequestSection`. Empty state already has its own `PULL REQUEST` eyebrow — leave it.

Add `"PullRequestHeader"` and `"PullRequestNumberMeta"` to `WorkspaceViewSmokeTests` name list if that list is exhaustive.

- [ ] **Step 4: Strip card chrome and theme the ComboBox**

In `PullRequestCard.axaml`:

1. Replace the outer `Border` (raised fill, corner radius, padding) with a root `StackPanel Spacing="9"` (padding lives on `sectionBody` elsewhere — match Session density: if the body feels tight, set `Padding="0"` on the sectionBody host and keep `Spacing="9"` only).
2. Remove the top row that shows muted "Pull request" + `NumberLabel` (section owns that). Keep a top row that is only the lifecycle badge when `CanDisplay`:

```xml
<Grid ColumnDefinitions="*,Auto" ColumnSpacing="8"
      IsVisible="{Binding CanDisplay}">
    <views:PullRequestStatusLabel Grid.Column="1" DataContext="{Binding LifecycleStatus}" />
</Grid>
```

3. On the ComboBox, add `Classes="kcapField"`:

```xml
<ComboBox x:Name="PullRequestSelector" Classes="kcapField"
          ItemsSource="{Binding Choices}" SelectedItem="{Binding Selected, Mode=TwoWay}"
          IsVisible="{Binding HasMultipleChoices}" HorizontalAlignment="Stretch"
          AutomationProperties.Name="Linked pull request">
```

Leave checks/reviews, notes, View PR / GitHub, sign-in / link actions unchanged.

- [ ] **Step 5: Run PR smoke + presentation tests**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*WorkContextViewSmokeTests/*pull_request*"
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*PullRequestPresentationTests/*"
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*PullRequestViewSmokeTests/*"
```

Expected: PASS. Fix any host that assumed `card.Content` was a `Border`.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/Views/WorkContextView.axaml src/Capacitor.App/Views/PullRequestCard.axaml test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs
git commit -m "$(cat <<'EOF'
[AI-3039] desktop: Section-shell the PR card and theme its picker

Status rows stay in the pane; the ComboBox uses kcapField like other fields.
EOF
)"
```

---

### Task 3: Issue section shell (no overflow)

**Files:**
- Modify: `src/Capacitor.App/Views/WorkContextView.axaml`
- Test: `test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs`
- Possibly: `test/Capacitor.App.Tests.Unit/WorkContextViewModelTests.cs` (visibility still via `IssueCard`)

**Interfaces:**
- Consumes: `HasSeparateIssue`, `Issue.Key`, `Issue.Title`, `Issue.OpenCommand`, `Issue.CanOpen`
- Produces: `IssueSection` with `IssueKey` / `IssueTitle` named TextBlocks; retire LinkCard template for this slot only (legacy PR link cards still use `LinkCard`)

- [ ] **Step 1: Rewrite issue smoke assertions**

Update `Key_only_items_and_untitled_issues_keep_each_distinct_key_visible` so the separate-issue path reads:

```csharp
var issueSection = host.Find<StackPanel>("IssueSection");
await Assert.That(issueSection.IsEffectivelyVisible).IsEqualTo(!inline);
if (!inline) {
    var linkKey = host.Find<TextBlock>("IssueKey");
    var linkTitle = host.Find<TextBlock>("IssueTitle");
    await Assert.That(linkKey.Text).IsEqualTo(issueKey);
    await Assert.That(linkKey.IsEffectivelyVisible).IsTrue();
    // Untitled separate issue: title control may be empty/collapsed
    await Assert.That(linkTitle.IsEffectivelyVisible).IsFalse();
}
```

Replace `Hovering_a_link_card_paints_no_chrome_outside_its_rounded_border` so it still pins legacy PR `LinkCards` hover (those still use the `LinkCard` template), **or** retarget it to open the issue title button and assert `copyValue` / transparent presenter (same invariant as other pane copy rows). Prefer keeping the legacy LinkCard hover test by showing a legacy-link fixture if one exists; otherwise retarget to Issue:

```csharp
var button = host.Find<Button>("IssueTitleButton");
// pointerover → PART_ContentPresenter alpha 0 (copyValue style)
```

Add a wrapping test:

```csharp
[Test]
[NotInParallel("AvaloniaSession")]
public async Task Issue_title_wraps_instead_of_sharing_a_horizontal_row_with_the_key() {
    await RunOnUiAsync(async () => {
        await using var host = new Host();
        // Use a read that yields HasSeparateIssue with a long title — same fixture path as
        // existing separate-issue tests in WorkContextViewModelTests / smoke Host.
        await host.ShowAsync(/* fixture with separate issue + long title */);
        var key = host.Find<TextBlock>("IssueKey");
        var title = host.Find<TextBlock>("IssueTitle");
        await Assert.That(title.TextWrapping).IsEqualTo(TextWrapping.Wrap);
        // Key is section meta, not a sibling in a horizontal StackPanel with the title
        await Assert.That(title.Parent).IsNotTypeOf<StackPanel>()
            .OrElse(() => ((StackPanel)title.Parent!).Orientation != Orientation.Horizontal
                || !((StackPanel)title.Parent!).Children.Contains(key));
        // Stronger: title's bounds width is within the pane content width
        var pane = host.Find<ScrollViewer>("PaneScroll");
        await Assert.That(title.Bounds.Width).IsLessThanOrEqualTo(pane.Bounds.Width);
    });
}
```

Use the project's real fixture helper for a separate issue (see `WorkContextViewModelTests` around `HasSeparateIssue` / `inline` arguments). Do not invent a new DTO shape.

- [ ] **Step 2: Run — expect fail**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*WorkContextViewSmokeTests/*issue*"
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*WorkContextViewSmokeTests/*Key_only*"
```

Expected: FAIL — `IssueSection` / `IssueKey` missing.

- [ ] **Step 3: Replace Issue ContentControl + LinkCard**

Replace:

```xml
<ContentControl x:Name="IssueCard" Content="{Binding Issue}" ContentTemplate="{StaticResource LinkCard}"
                Margin="0,18,0,0" IsVisible="{Binding HasSeparateIssue}" />
```

with:

```xml
<StackPanel x:Name="IssueSection" Margin="0,18,0,0" IsVisible="{Binding HasSeparateIssue}">
    <Button Classes="sectionHeader">
        <Grid ColumnDefinitions="*,Auto" ColumnSpacing="10">
            <TextBlock Text="ISSUE" Classes="eyebrow" />
            <TextBlock x:Name="IssueKey" Grid.Column="1" Text="{Binding Issue.Key}" Classes="sectionMeta"
                       IsVisible="{Binding Issue.Key, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
        </Grid>
    </Button>
    <Border Classes="sectionBody">
        <Button x:Name="IssueTitleButton" Classes="copyValue"
                Command="{Binding Issue.OpenCommand}"
                IsVisible="{Binding Issue.Title, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
                ToolTip.Tip="{Binding Issue.Url}"
                HorizontalAlignment="Stretch">
            <TextBlock x:Name="IssueTitle" Text="{Binding Issue.Title}" FontSize="13" LineHeight="16"
                       TextWrapping="Wrap" Foreground="{StaticResource KcapTextBrush}" />
        </Button>
    </Border>
</StackPanel>
```

Keep `x:Name="IssueCard"` somehow for older tests: either alias `IssueSection` as the control tests find, or update every `IssueCard` find to `IssueSection`. Prefer updating finds to `IssueSection` and the workspace name list.

Update `WorkContextViewModelTests` visibility asserts that use `FindControl<ContentControl>("IssueCard")` to `FindControl<StackPanel>("IssueSection")` (or `Control`).

Leave the `LinkCard` DataTemplate in Resources — still used by legacy `LinkCards`.

- [ ] **Step 4: Run issue + related VM tests**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*WorkContextViewSmokeTests/*"
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*WorkContextViewModelTests/*"
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*WorkspaceViewSmokeTests/*"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Views/WorkContextView.axaml test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs test/Capacitor.App.Tests.Unit/WorkContextViewModelTests.cs test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs
git commit -m "$(cat <<'EOF'
[AI-3039] desktop: Section-shell the separate issue without overflow

Key is section meta; the title wraps in the hairline body.
EOF
)"
```

---

### Task 4: CHANGES note and full suite check

**Files:**
- Modify: `docs/CHANGES.md` (new top entry after the file's lead-in)
- Verify only

- [ ] **Step 1: Add a CHANGES entry**

Insert after the introductory paragraphs (before the current first `##` heading):

```markdown
## The work-context pane keeps PR status inside section shells

The chrome overhaul taught the pane a shared section grammar, then briefly replaced
the pull-request card with a title that only opened the reader — checks and review
left the sidebar. The card returned, but as a raised Fluent island. Pull request and
issue now use the same eyebrow and left hairline as Session: the PR body still shows
lifecycle, checks, review, and a `kcapField` picker when more than one PR is linked;
the issue key is meta and its title wraps. Who's on it sits under the work item —
it is about the item, not the session — and Session is last.
```

Keep it present-tense and free of ticket ids / "used to" narration beyond the one sentence that names why status must stay (allowed as a live constraint, not change history — shorten if it reads as history).

- [ ] **Step 2: Run the app unit suite**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj
```

Expected: all passed (ignore known locale-only flakes only if already documented on the machine).

- [ ] **Step 3: Commit**

```bash
git add docs/CHANGES.md
git commit -m "$(cat <<'EOF'
[AI-3039] docs: Note work-context PR/issue section shells

EOF
)"
```

---

## Self-review (plan vs spec)

| Spec requirement | Task |
|------------------|------|
| Order: work item → Who's on it → PR → issue → subagents → session | Task 1 |
| PR section shell; keep guts; `kcapField` picker | Task 2 |
| Selection unchanged / no merge preference | Task 2 (no code path change) |
| Issue section shell; wrap; no overflow | Task 3 |
| Out of scope (tab, collapse-merged, overview-all) | Not tasked |
| CHANGES | Task 4 |

No TBD/placeholder steps remain after fixing the order-test helper to a single `At` walk.
