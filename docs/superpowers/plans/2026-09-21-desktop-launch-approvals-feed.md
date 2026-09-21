# Launch-approvals feed Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the desktop launcher’s Activity chip to Launches and replace the consent-log spreadsheet with a readable feed, including middle-ellipsis emails that keep the domain.

**Architecture:** Keep `ActivityViewModel`’s refresh / Complete / file-read contracts. Reshape `ActivityRow` into feed fields (`Outcome`, `PrimaryDetail`, `SecondaryLine`, tips) with pure projection helpers (truncate + line join). Rewrite the flyout markup in `MainWindow.axaml` to bind those fields; keep `x:Name="ActivityButton"` so smoke tests stay stable.

**Tech Stack:** .NET 10, Avalonia 11, ReactiveUI, TUnit + Avalonia headless (`AvaloniaSession`).

**Spec:** `docs/superpowers/specs/2026-09-21-desktop-launch-approvals-feed-design.md` (Linear AI-3061).

## Global Constraints

- **Worktree:** `/Users/nortonandreev/Documents/GitHub/kcap-cli/.worktrees/desktop-activity-audit`, branch `norton/ai-3061-desktop-launch-approvals-feed`. Run every command from there.
- **No Linear ids in `.cs` / comments.** Id lives in the PR and this plan only.
- **Comments scarce:** no ticket narration, no “§”, no change history in comments.
- **Copy, verbatim:** chip `Launches`; flyout title `Launch approvals`; empty primary `No launches decided yet`; empty secondary `Allow and deny choices from consent prompts show up here.`; outcomes `Allowed` / `Denied`.
- **Keep control name** `ActivityButton` (and keep `ActivityViewModel` type name). Rename only user-visible strings.
- **Tests:** `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/<Class>/<Method>*"` — never `--filter`. Avalonia tests stay `[NotInParallel("AvaloniaSession")]`.
- **Commits:** imperative subject ≤ 80 chars including trailing `(#NNN)` GitHub issue reference when known, no Linear ids in subject, no `Co-authored-by`. Optional body ≤ 5 lines. Stage by explicit path.
- **Do not change** consent reader, refresh gates, source-label map, or prompt window.

---

## File structure

**Modify**
- `src/Capacitor.App/ViewModels/ActivityViewModel.cs` — `ActivityRow` shape; `ToRow`; `TruncateRequester` / `MiddleTruncate` / line builders (internal static for tests).
- `src/Capacitor.App/Views/MainWindow.axaml` — chip label, empty copy, feed template (drop 7-column header/grid).
- `test/Capacitor.App.Tests.Unit/ActivityViewModelTests.cs` — mapping + truncate tests.
- `docs/CHANGES.md` — short entry at top explaining why feed-not-table / Launches-not-Activity.

**Unchanged**
- `MainWindow.axaml.cs` polling gate (`ActivityButton` name).
- `Converters.cs` `OutcomeBrushConverter` (still keys off `IsAllowed`).
- Smoke `Activity_polls_only_while_open_…` (still finds `ActivityButton`).

---

### Task 1: Truncation + feed projection helpers

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ActivityViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/ActivityViewModelTests.cs`

**Interfaces:**
- Produces:
  - `internal static string MiddleTruncate(string value, int head = 10, int tail = 8)`
  - `internal static string TruncateRequester(string value, int localHead = 10, int head = 10, int tail = 8)`
  - `internal static string OutcomeLabelOf(string outcome)` → `Allowed` / `Denied` / else verbatim
  - `internal static string? KindDetailOf(string kind)` → null when `agent`, else `KindLabelOf`
  - `internal static string PrimaryDetailOf(string vendor, string kind)` → vendor, optionally ` · {kindLabel}`
  - `internal static string SecondaryLineOf(string requesterDisplay, string repoLeaf, string sourceLabel)` → join with ` · `, omit source when label is `you`

- [ ] **Step 1: Write the failing tests**

Add to `ActivityViewModelTests.cs` (no Avalonia needed for these):

```csharp
[Test]
[Arguments("short@co.io", "short@co.io")]
[Arguments("very.long.local-part@company.com", "very.long.…@company.com")]
[Arguments("Ada Lovelace", "Ada Lovelace")]
[Arguments("abcdefghijklmnop", "abcdefghij…ijklmnop")] // head 10 + … + tail 8 → wait: head+tail+1=19, len 16 → no truncate
public async Task TruncateRequester_cases(string input, string expected) {
    // Use explicit lengths matching production defaults (localHead=10, head=10, tail=8).
    // For "abcdefghijklmnop" (16 chars): 16 <= 10+8+1 → unchanged.
    await Assert.That(ActivityViewModel.TruncateRequester(input)).IsEqualTo(expected);
}

[Test]
public async Task TruncateRequester_keeps_domain_when_local_is_long() {
    await Assert.That(ActivityViewModel.TruncateRequester("verylonglocalpartname@example.org", localHead: 8))
        .IsEqualTo("verylong…@example.org");
}

[Test]
public async Task TruncateRequester_middle_truncates_non_email() {
    await Assert.That(ActivityViewModel.TruncateRequester("abcdefghijklmnopqrstuvwxyz", head: 4, tail: 4))
        .IsEqualTo("abcd…wxyz");
}

[Test]
[Arguments("allowed", "Allowed")]
[Arguments("denied", "Denied")]
[Arguments("weird", "weird")]
public async Task Outcome_labels(string raw, string expected) {
    await Assert.That(ActivityViewModel.OutcomeLabelOf(raw)).IsEqualTo(expected);
}

[Test]
public async Task PrimaryDetail_omits_agent_kind() {
    await Assert.That(ActivityViewModel.PrimaryDetailOf("claude", "agent")).IsEqualTo("claude");
    await Assert.That(ActivityViewModel.PrimaryDetailOf("codex", "review-flow")).IsEqualTo("codex · Review flow");
}

[Test]
public async Task SecondaryLine_omits_you_source() {
    await Assert.That(ActivityViewModel.SecondaryLineOf("ada@x.com", "kcap-cli", "you"))
        .IsEqualTo("ada@x.com · kcap-cli");
    await Assert.That(ActivityViewModel.SecondaryLineOf("ada@x.com", "kcap-cli", "rule"))
        .IsEqualTo("ada@x.com · kcap-cli · rule");
}
```

Fix the weak `Arguments` case: drop the ambiguous `"abcdefghijklmnop"` row; rely on the dedicated middle-truncate test.

- [ ] **Step 2: Run tests — expect FAIL (helpers missing)**

```bash
cd /Users/nortonandreev/Documents/GitHub/kcap-cli/.worktrees/desktop-activity-audit
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ActivityViewModelTests/TruncateRequester*"
```

Expected: compile error or missing method.

- [ ] **Step 3: Implement helpers on `ActivityViewModel`**

```csharp
internal static string MiddleTruncate(string value, int head = 10, int tail = 8) {
    if (value.Length <= head + tail + 1) return value;
    return string.Concat(value.AsSpan(0, head), "…", value.AsSpan(value.Length - tail));
}

internal static string TruncateRequester(string value, int localHead = 10, int head = 10, int tail = 8) {
    var at = value.LastIndexOf('@');
    if (at > 0 && at < value.Length - 1) {
        var local = value.AsSpan(0, at);
        var domain = value.AsSpan(at + 1);
        // Domain alone too long for a secondary line — fall back to whole-string middle truncate.
        if (domain.Length > 48) return MiddleTruncate(value, head, tail);
        if (local.Length <= localHead) return value;
        return string.Concat(local[..localHead], "…@", domain);
    }
    return MiddleTruncate(value, head, tail);
}

internal static string OutcomeLabelOf(string outcome) => outcome switch {
    "allowed" => "Allowed",
    "denied"  => "Denied",
    _         => outcome,
};

internal static string PrimaryDetailOf(string vendor, string kind) {
    var kindLabel = kind == "agent" ? null : ConsentPromptViewModel.KindLabelOf(kind);
    return kindLabel is null ? vendor : $"{vendor} · {kindLabel}";
}

internal static string SecondaryLineOf(string requesterDisplay, string repoLeaf, string sourceLabel) {
    if (sourceLabel == "you") return $"{requesterDisplay} · {repoLeaf}";
    return $"{requesterDisplay} · {repoLeaf} · {sourceLabel}";
}
```

- [ ] **Step 4: Run truncate / label tests — expect PASS**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ActivityViewModelTests/*"
```

(May still fail on old row-mapping assertions until Task 2 — if so, run only the new method names.)

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/ActivityViewModel.cs test/Capacitor.App.Tests.Unit/ActivityViewModelTests.cs
git commit -m "$(cat <<'EOF'
Add launch-approvals truncation and line helpers (#1089)

Emails keep the domain; agent kind and "you" source drop out of the feed lines.
EOF
)"
```

---

### Task 2: Reshape `ActivityRow` and `ToRow`

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ActivityViewModel.cs`
- Modify: `test/Capacitor.App.Tests.Unit/ActivityViewModelTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record ActivityRow(
    string Time,
    string TimeTip,
    string Outcome,
    bool IsAllowed,
    string PrimaryDetail,
    string SecondaryLine,
    string RequesterFull,
    string RepoFull);
```

- Consumes: Task 1 helpers; existing `RequesterOf`, `FormatTime`, `SourceLabelOf`, `RepoLabel.Leaf`, `ConsentPromptViewModel.KindLabelOf`.

- [ ] **Step 1: Update the mapping test to the new shape (fails until ToRow changes)**

Replace assertions in `Rows_map_records_with_fallbacks_and_source_labels` roughly as:

```csharp
await Assert.That(first.Time).IsEqualTo(expectedTime);
await Assert.That(first.TimeTip).IsEqualTo(expectedTip);
await Assert.That(first.Outcome).IsEqualTo("Allowed");
await Assert.That(first.IsAllowed).IsTrue();
await Assert.That(first.PrimaryDetail).IsEqualTo("codex · Review flow");
await Assert.That(first.SecondaryLine).IsEqualTo("Ada Lovelace · tender-honking-pebble · rule");
await Assert.That(first.RequesterFull).IsEqualTo("Ada Lovelace");
await Assert.That(first.RepoFull).IsEqualTo("/repos/kcap-cli/.claude/worktrees/tender-honking-pebble");

await Assert.That(second.Time).IsEqualTo("not-a-timestamp");
await Assert.That(second.Outcome).IsEqualTo("Denied");
await Assert.That(second.IsAllowed).IsFalse();
await Assert.That(second.PrimaryDetail).IsEqualTo("claude"); // agent kind omitted
await Assert.That(second.SecondaryLine).IsEqualTo("unknown · kcap-cli · timeout");
await Assert.That(second.RequesterFull).IsEqualTo("unknown");
```

Add one email case (can be a separate small Avalonia or pure ToRow test via a complete read):

```csharp
[Test]
[NotInParallel("AvaloniaSession")]
public async Task Rows_middle_truncate_email_requester() {
    var row = await AvaloniaSession.DispatchAsync(async () => {
        var reader = new ScriptedReader();
        reader.Set(new ConsentLogReadResult([
            Rec(requesterDisplay: "very.long.local-part@company.com", kind: "agent",
                repoPath: "/repos/kcap-cli", vendor: "claude", outcome: "allowed", source: "prompt_user")
        ], true));
        var vm = new ActivityViewModel(reader.Read, new ScriptedStat().Get, new FakeTicker());
        vm.OnTabVisibleChanged(true);
        await vm.PendingRefreshForTesting!;
        return vm.Rows[0];
    });

    await Assert.That(row.RequesterFull).IsEqualTo("very.long.local-part@company.com");
    await Assert.That(row.SecondaryLine).IsEqualTo("very.long.…@company.com · kcap-cli"); // source you omitted
    await Assert.That(row.PrimaryDetail).IsEqualTo("claude");
    await Assert.That(row.Outcome).IsEqualTo("Allowed");
}
```

- [ ] **Step 2: Run mapping tests — expect FAIL**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ActivityViewModelTests/Rows_*"
```

- [ ] **Step 3: Replace `ActivityRow` and `ToRow`**

```csharp
public sealed record ActivityRow(
    string Time, string TimeTip, string Outcome, bool IsAllowed,
    string PrimaryDetail, string SecondaryLine, string RequesterFull, string RepoFull);

static ActivityRow ToRow(ConsentDecisionRecord r) {
    var (time, tip) = FormatTime(r.DecidedAt);
    var requester = RequesterOf(r);
    var source = SourceLabelOf(r.Source);
    var leaf = RepoLabel.Leaf(r.RepoPath);
    return new(
        time, tip,
        OutcomeLabelOf(r.Outcome),
        r.Outcome == "allowed",
        PrimaryDetailOf(r.Vendor, r.Kind),
        SecondaryLineOf(TruncateRequester(requester), leaf, source),
        requester,
        r.RepoPath);
}
```

Update the `ActivityRow` doc comment to describe feed lines (not table columns).

- [ ] **Step 4: Run all ActivityViewModelTests — expect PASS**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ActivityViewModelTests/*"
```

Expected: all green. (Build may fail until Task 3 if XAML still binds old properties — implement Task 3 in the same sitting if the project compiles AXAML at build time, which Avalonia does. Prefer Task 3 next immediately; if you commit Task 2 alone, include a minimal XAML stub that binds the new names so the app project builds.)

**Build note:** Avalonia compiles bindings; after changing `ActivityRow`, `MainWindow.axaml` will fail compile until Task 3. Do Task 3 before committing Task 2, or commit Tasks 2+3 together. Prefer **one commit for Tasks 2+3** if the tree cannot build mid-way — then skip Task 2’s separate commit and use Task 3’s commit message covering both.

- [ ] **Step 5: Commit only if the solution builds**; otherwise proceed to Task 3 without committing.

---

### Task 3: Flyout markup + copy + CHANGES

**Files:**
- Modify: `src/Capacitor.App/Views/MainWindow.axaml` (Activity chip + flyout block ~lines 52–157)
- Modify: `docs/CHANGES.md` (new top entry)
- Touch smoke only if empty-state text is asserted somewhere (today it is not by name for copy).

**Interfaces:**
- Consumes: Task 2 `ActivityRow` fields.
- Keeps: `x:Name="ActivityButton"`, `ActivityEmptyText`, `ActivityItems`; drop `ActivityHeader` (or leave unused — prefer delete).

- [ ] **Step 1: Rewrite the flyout content**

Chip `TextBlock`: `Launches`.

Empty stack:
- `ActivityEmptyText` → `No launches decided yet`
- secondary → `Allow and deny choices from consent prompts show up here.`

Non-empty panel:

```xml
<Border Width="460" MaxHeight="420" Padding="14,12">
  <Grid>
    <!-- empty stack unchanged structure, new copy -->
    <DockPanel IsVisible="{Binding !Activity.IsEmpty}">
      <TextBlock DockPanel.Dock="Top" Text="Launch approvals"
                 FontSize="12.5" FontWeight="SemiBold"
                 Foreground="{StaticResource KcapMutedBrush}"
                 Margin="4,0,4,10" />
      <ScrollViewer VerticalScrollBarVisibility="Auto"
                    HorizontalScrollBarVisibility="Disabled">
        <ItemsControl x:Name="ActivityItems" ItemsSource="{Binding Activity.Rows}">
          <ItemsControl.Styles>
            <Style Selector="ItemsControl > ContentPresenter">
              <Setter Property="HorizontalAlignment" Value="Stretch" />
            </Style>
          </ItemsControl.Styles>
          <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="vm:ActivityRow">
              <Border BorderBrush="{StaticResource KcapBorderBrush}"
                      BorderThickness="0,0,0,1" Padding="4,10">
                <StackPanel Spacing="2">
                  <StackPanel Orientation="Horizontal" Spacing="0">
                    <TextBlock Text="{Binding Outcome}" FontSize="12.5" FontWeight="SemiBold"
                               Foreground="{Binding IsAllowed, Converter={x:Static views:OutcomeBrushConverter.Instance}}" />
                    <TextBlock Text=" · " FontSize="12.5"
                               Foreground="{StaticResource KcapTextBrush}" />
                    <TextBlock Text="{Binding PrimaryDetail}" FontSize="12.5"
                               Foreground="{StaticResource KcapTextBrush}"
                               TextTrimming="CharacterEllipsis" />
                  </StackPanel>
                  <TextBlock Text="{Binding SecondaryLine}" FontSize="12"
                             Foreground="{StaticResource KcapMutedBrush}"
                             TextTrimming="CharacterEllipsis"
                             ToolTip.Tip="{Binding RequesterFull}" />
                  <TextBlock Text="{Binding Time}" FontSize="11"
                             Foreground="{StaticResource KcapFaintBrush}"
                             ToolTip.Tip="{Binding TimeTip}" />
                </StackPanel>
              </Border>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </ScrollViewer>
    </DockPanel>
  </Grid>
</Border>
```

Also set a `ToolTip.Tip` on the secondary line that includes full requester (and optionally repo) — minimum: `RequesterFull` as specified. If useful, combine tip in the VM later; out of scope to add `SecondaryTip` unless smoke needs it. Prefer a second tooltip on an invisible element is wrong — for repo, set:

```xml
ToolTip.Tip="{Binding RepoFull}"
```

only loses requester tip. Spec wants full requester on hover; repo full path also. Add `SecondaryTip` on the row:

```csharp
// In ToRow, if you want both in one tip:
// SecondaryTip = requester + "\n" + r.RepoPath
```

**Do this in Task 2’s record** if not already present:

```csharp
public sealed record ActivityRow(
    ...,
    string SecondaryTip); // $"{requester}\n{r.RepoPath}" or single-line with · 
```

Update mapping tests to assert `SecondaryTip` contains both. Bind `ToolTip.Tip="{Binding SecondaryTip}"` on the secondary `TextBlock`.

If Task 2 already committed without `SecondaryTip`, add it in this task with a tiny test update.

- [ ] **Step 2: Build the app project**

```bash
dotnet build src/Capacitor.App/Capacitor.App.csproj
```

Expected: 0 errors.

- [ ] **Step 3: Run Activity unit + Activity smoke gate**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ActivityViewModelTests/*"
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/MainWindowSmokeTests/Activity_polls_only_while_open*"
```

Expected: PASS.

- [ ] **Step 4: Add `docs/CHANGES.md` entry at the top**

```markdown
## Launcher “Launches” is the consent decision log, not session activity

The chip formerly labeled Activity opened the local allow/deny log for daemon
launches. The name and a seven-column table made that hard to read. The chip is
**Launches**, the flyout title is **Launch approvals**, and each decision is a
short feed row (outcome · vendor · kind; requester · repo · source; time). Email
requesters keep the domain via middle ellipsis. The file, poll, and Complete
rules are unchanged.
```

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/ActivityViewModel.cs \
        src/Capacitor.App/Views/MainWindow.axaml \
        test/Capacitor.App.Tests.Unit/ActivityViewModelTests.cs \
        docs/CHANGES.md
git commit -m "$(cat <<'EOF'
Show launch approvals as a readable feed (#1089)

The Activity spreadsheet hid that this is consent history; Launches names it
and middle-ellipsis keeps email domains.
EOF
)"
```

---

## Self-review (plan vs spec)

| Spec item | Task |
|---|---|
| Chip Launches / title Launch approvals / empty copy | Task 3 |
| Feed rows (outcome · vendor · kind; secondary; time) | Tasks 2–3 |
| Omit agent kind / omit you source | Task 1–2 |
| Email middle-ellipsis keeps domain | Task 1–2 |
| Width ~420–480, no column headers | Task 3 |
| Unchanged reader/refresh/Complete/200 | no code change |
| No badge / no broader Activity | out of scope |
| Unit + smoke | Tasks 1–3 |

No placeholders left. `SecondaryTip` must land with the row reshape so XAML has one tooltip for requester+repo — fold into Task 2 record before Task 3 binds it.

---

## Execution handoff

Plan complete and saved to `docs/superpowers/plans/2026-09-21-desktop-launch-approvals-feed.md`.

**Two execution options:**

1. **Subagent-Driven (recommended)** — fresh subagent per task, review between tasks  
2. **Inline Execution** — execute tasks in this session with checkpoints  

Which approach?
