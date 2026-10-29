# Desktop chat bubble chrome (AI-3051) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Left-align system chat cards, center prompt cards, share kind-header chrome with Material-like category icons, and stop Stretch+MaxWidth from centering tool groups.

**Architecture:** Keep existing row DataTemplates. Add `chatSystemCard` / `chatPromptCard` style classes, a small `ChatKindHeader` UserControl (icon + label + optional status slot), and `ToolCategoryIcons` PathData strings (parse per call like `VendorIcons`). Wire Note/tool groups left; pending host Center; multi-call summary prefixes the first settled category’s icon.

**Tech Stack:** .NET 10, Avalonia 12 (headless), ReactiveUI, TUnit on Microsoft Testing Platform.

**Spec:** `docs/superpowers/specs/2026-09-21-desktop-chat-bubble-chrome-design.md`

## Global Constraints

- Comments: scarce; no ticket ids, change narration, or design coordinates (CLAUDE.md Comments).
- Commit subjects: one imperative clause, ≤80 chars including `(#1083)`.
- App UI tests: `RunOnUiAsync` + `[NotInParallel("AvaloniaSession")]`. Throwaways via `[TempDir]`.
- Status colours (`KcapSuccess*` / `KcapWarning*`) only on outcome/attention — kind icons stay muted (`KcapMutedBrush`).
- Never `Stretch` + `MaxWidth` on system cards (Avalonia centers the leftover).
- Icons: muted stroke Paths ~14×14; Material-*like*, not pixel MudBlazor copies; parse PathData per call (Geometry thread affinity — same rule as `VendorIcons`).
- Run suite: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/<ClassName>/*'`.

---

## File structure

| File | Responsibility |
|---|---|
| `src/Capacitor.App/Views/ToolCategoryIcons.cs` (create) | PathData strings for every `ToolCategory` + fixed labels `Note` / `Permission` / `Question`. |
| `test/Capacitor.App.Tests.Unit/ToolCategoryIconsTests.cs` (create) | Completeness + non-empty parse for every category and fixed label. |
| `src/Capacitor.App/Views/ChatKindHeader.axaml` + `.axaml.cs` (create) | Shared header: icon Path + `toolKindChip` label + optional status `ContentPresenter`. |
| `src/Capacitor.App/Views/ChatBubbleStyles.axaml` (create) | `chatSystemCard` / `chatPromptCard` (+ header layout if needed); merged from `App.axaml`. |
| `src/Capacitor.App/App.axaml` (modify) | `ResourceInclude` for `ChatBubbleStyles.axaml`. |
| `src/Capacitor.App/ViewModels/ChatItems.cs` (modify) | `ToolGroupItem.HeaderIconData` (lone category or first settled); raise on recompute. |
| `test/Capacitor.App.Tests.Unit/ToolGroupItemTests.cs` (modify) | Pin `HeaderIconData` for lone and multi-call. |
| `src/Capacitor.App/Views/ChatTabView.axaml` (modify) | System shells + `ChatKindHeader`; Left on tool groups; drop MinWidth 320; multi-call icon. |
| `src/Capacitor.App/Views/PendingCardTemplates.axaml` (modify) | `chatPromptCard` + `ChatKindHeader` on Permission / Question / ACP. |
| `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs` (modify) | Alignment + header icon presence; update chip finders if needed. |
| `docs/CHANGES.md` (modify) | Why left/shared header/icons. |

---

### Task 1: `ToolCategoryIcons` map

**Files:**
- Create: `src/Capacitor.App/Views/ToolCategoryIcons.cs`
- Create: `test/Capacitor.App.Tests.Unit/ToolCategoryIconsTests.cs`

**Interfaces:**
- Produces: `Capacitor.App.Views.ToolCategoryIcons` with:
  - `public static string ForCategory(ToolCategory category)`
  - `public static string ForFixedLabel(string label)` — non-empty only for `Note`, `Permission`, `Question` (ordinal)
  - Both return PathData strings in a 24×24 viewBox suitable for stroke Paths; never return null (unknown fixed label → `""`).

- [ ] **Step 1: Write the failing tests**

```csharp
using Avalonia.Media;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class ToolCategoryIconsTests {
    [Test]
    public async Task Every_category_has_parseable_path_data() {
        foreach (ToolCategory category in Enum.GetValues<ToolCategory>()) {
            var data = ToolCategoryIcons.ForCategory(category);
            await Assert.That(data).IsNotEmpty().Because($"{category}");
            await Assert.That(Geometry.Parse(data)).IsNotNull().Because($"{category}");
        }
    }

    [Test]
    [Arguments("Note")]
    [Arguments("Permission")]
    [Arguments("Question")]
    public async Task Fixed_labels_have_parseable_path_data(string label) {
        var data = ToolCategoryIcons.ForFixedLabel(label);
        await Assert.That(data).IsNotEmpty();
        await Assert.That(Geometry.Parse(data)).IsNotNull();
    }

    [Test]
    public async Task Unknown_fixed_label_is_empty() {
        await Assert.That(ToolCategoryIcons.ForFixedLabel("You")).IsEmpty();
        await Assert.That(ToolCategoryIcons.ForFixedLabel("")).IsEmpty();
    }
}
```

- [ ] **Step 2: Run tests — expect fail (type missing)**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/ToolCategoryIconsTests/*'
```

Expected: compile fail or missing `ToolCategoryIcons`.

- [ ] **Step 3: Implement `ToolCategoryIcons`**

Create `src/Capacitor.App/Views/ToolCategoryIcons.cs`:

```csharp
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Stroke PathData (24×24) for chat kind headers. Material-like at category grain — same
/// vocabulary as the web's GetToolIcon, not per vendor tool name. Strings only: Geometry is
/// an AvaloniaObject with thread affinity (see VendorIcons).
public static class ToolCategoryIcons {
    public static string ForCategory(ToolCategory category) => category switch {
        ToolCategory.Read      => "M6,3 H15 L19,7 V21 H6 Z M15,3 V7 H19",           // Description
        ToolCategory.Edit      => "M4,20 H8 L18.5,9.5 14.5,5.5 4,16 Z M13,7 L17,11", // EditNote
        ToolCategory.Command   => "M3,5 H21 V17 H3 Z M8,20 H16 M12,17 V20",          // Terminal
        ToolCategory.Search    => "M10,4 A6,6 0 1 1 10,16 A6,6 0 1 1 10,4 M15,15 L20,20",
        ToolCategory.WebSearch => "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M3,12 H21 M12,3 C8,8 8,16 12,21 C16,16 16,8 12,3",
        ToolCategory.Fetch     => "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M3,12 H21",
        ToolCategory.Skill     => "M12,3 L13.5,9 H20 L15,12.5 17,19 12,15 7,19 9,12.5 4,9 H10.5 Z", // sparkle
        ToolCategory.Agent     => "M8,10 A3,3 0 1 0 8,4 A3,3 0 1 0 8,10 M16,10 A3,3 0 1 0 16,4 A3,3 0 1 0 16,10 M4,20 C4,16 20,16 20,20",
        ToolCategory.Plan      => "M5,5 H19 M5,10 H19 M5,15 H14 M4,4 H6 V6 H4 Z M4,9 H6 V11 H4 Z M4,14 H6 V16 H4 Z",
        ToolCategory.Question  => "M6,6 H18 V16 H13 L9,20 V16 H6 Z M12,9 V10 M12,13 H12.01",
        ToolCategory.Other     => "M14.5,4 L19,9 9.5,18.5 5,19 5.5,14.5 Z M12,7 L16,11", // Build
        _                      => "M14.5,4 L19,9 9.5,18.5 5,19 5.5,14.5 Z M12,7 L16,11",
    };

    public static string ForFixedLabel(string label) => label switch {
        "Note"       => "M6,3 H15 L19,7 V21 H6 Z M15,3 V7 H19 M8,12 H16 M8,16 H14",
        "Permission" => "M7,10 V8 A5,5 0 0 1 17,8 V10 H19 V21 H5 V10 Z M12,14 V17",
        "Question"   => ForCategory(ToolCategory.Question),
        _            => "",
    };
}
```

If any `Geometry.Parse` fails in tests, simplify that path until it parses; keep the category→role mapping.

- [ ] **Step 4: Run tests — expect pass**

Same command as Step 2. Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Views/ToolCategoryIcons.cs test/Capacitor.App.Tests.Unit/ToolCategoryIconsTests.cs
git commit -m "$(cat <<'EOF'
Add Material-like path data for chat kind icons (#1083)

EOF
)"
```

---

### Task 2: `ChatKindHeader` + card style classes

**Files:**
- Create: `src/Capacitor.App/Views/ChatKindHeader.axaml`
- Create: `src/Capacitor.App/Views/ChatKindHeader.axaml.cs`
- Create: `src/Capacitor.App/Views/ChatBubbleStyles.axaml`
- Modify: `src/Capacitor.App/App.axaml` (MergedDictionaries)

**Interfaces:**
- Produces: `Capacitor.App.Views.ChatKindHeader` : `UserControl` with styled properties:
  - `string Label` (`LabelProperty`)
  - `string IconData` (`IconDataProperty`) — empty hides the Path
  - `object? Status` (`StatusProperty`) — content for the trailing slot
- Produces styles: `Border.chatSystemCard`, `Border.chatPromptCard` (and `ContentControl.chatPromptCard` for the pending host).

- [ ] **Step 1: Add `ChatKindHeader`**

`ChatKindHeader.axaml.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

public partial class ChatKindHeader : UserControl {
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ChatKindHeader, string>(nameof(Label), "");

    public static readonly StyledProperty<string> IconDataProperty =
        AvaloniaProperty.Register<ChatKindHeader, string>(nameof(IconData), "");

    public static readonly StyledProperty<object?> StatusProperty =
        AvaloniaProperty.Register<ChatKindHeader, object?>(nameof(Status));

    public ChatKindHeader() => InitializeComponent();

    public string Label {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string IconData {
        get => GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public object? Status {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
}
```

`ChatKindHeader.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="Capacitor.App.Views.ChatKindHeader"
             x:Name="Root">
    <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="8">
        <Panel Width="14" Height="14" VerticalAlignment="Center"
               IsVisible="{Binding #Root.IconData, Converter={x:Static StringConverters.IsNotNullOrEmpty}}">
            <Path Width="14" Height="14" Stretch="Uniform"
                  Data="{Binding #Root.IconData}"
                  Stroke="{StaticResource KcapMutedBrush}" StrokeThickness="1.4"
                  StrokeLineCap="Round" StrokeJoin="Round"
                  HorizontalAlignment="Center" VerticalAlignment="Center" />
        </Panel>
        <TextBlock Grid.Column="1" Classes="toolKindChip" Text="{Binding #Root.Label}"
                   VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
        <ContentPresenter Grid.Column="2" Content="{Binding #Root.Status}"
                          VerticalAlignment="Center"
                          IsVisible="{Binding #Root.Status, Converter={x:Static ObjectConverters.IsNotNull}}" />
    </Grid>
</UserControl>
```

- [ ] **Step 2: Add `ChatBubbleStyles.axaml` and merge it**

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style Selector="Border.chatSystemCard">
        <Setter Property="HorizontalAlignment" Value="Left" />
        <Setter Property="MaxWidth" Value="660" />
        <Setter Property="Padding" Value="14,10" />
        <Setter Property="CornerRadius" Value="10" />
        <Setter Property="Background" Value="{StaticResource KcapSurfaceBrush}" />
        <Setter Property="BorderBrush" Value="{StaticResource KcapBorderBrush}" />
        <Setter Property="BorderThickness" Value="1" />
    </Style>
    <Style Selector="Border.chatPromptCard">
        <Setter Property="HorizontalAlignment" Value="Stretch" />
        <Setter Property="Padding" Value="14,10" />
        <Setter Property="CornerRadius" Value="10" />
        <Setter Property="Background" Value="{StaticResource KcapSurfaceRaisedBrush}" />
        <Setter Property="BorderBrush" Value="{StaticResource KcapBorderBrush}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Margin" Value="0,0,0,6" />
    </Style>
    <Style Selector="ContentControl.chatPromptCard">
        <Setter Property="HorizontalAlignment" Value="Center" />
        <Setter Property="MaxWidth" Value="660" />
    </Style>
</ResourceDictionary>
```

In `App.axaml` MergedDictionaries, beside PendingCardTemplates:

```xml
<ResourceInclude Source="avares://Kurrent Capacitor/Views/ChatBubbleStyles.axaml" />
```

(Confirm the assembly name matches the existing PendingCardTemplates include.)

- [ ] **Step 3: Build the app project**

```bash
dotnet build src/Capacitor.App/Capacitor.App.csproj
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Capacitor.App/Views/ChatKindHeader.axaml \
        src/Capacitor.App/Views/ChatKindHeader.axaml.cs \
        src/Capacitor.App/Views/ChatBubbleStyles.axaml \
        src/Capacitor.App/App.axaml
git commit -m "$(cat <<'EOF'
Add shared chat kind header and card style classes (#1083)

EOF
)"
```

---

### Task 3: `ToolGroupItem.HeaderIconData`

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatItems.cs`
- Modify: `test/Capacitor.App.Tests.Unit/ToolGroupItemTests.cs`

**Interfaces:**
- Consumes: `ToolCategoryIcons.ForCategory`
- Produces: `ToolGroupItem.HeaderIconData` (`string`) — lone call’s category, else first settled call’s category in arrival order; `""` when no calls / none settled for multi with no settlement yet (live-only multi may use first call’s category so the summary icon exists once the header shows — when `ShowsSummaryHeader`, at least one settled exists, so first settled is defined).

- [ ] **Step 1: Write failing assertions in `ToolGroupItemTests`**

Add to `A_lone_settled_call_stays_visible_without_summary_chrome` (after KindChip assert):

```csharp
await Assert.That(group.HeaderIconData).IsEqualTo(ToolCategoryIcons.ForCategory(ToolCategory.Command));
```

Add a new test:

```csharp
[Test]
[NotInParallel("AvaloniaSession")]
public async Task Header_icon_follows_the_first_settled_category() {
    await RunOnUiAsync(async () => {
        var group = new ToolGroupItem();
        var a = Call("Bash", ToolCategory.Command);
        var b = Call("Read", ToolCategory.Read);
        group.Add(a);
        group.Add(b);
        b.Outcome = ToolOutcome.Done;
        await Assert.That(group.HeaderIconData).IsEqualTo(ToolCategoryIcons.ForCategory(ToolCategory.Read));
        a.Outcome = ToolOutcome.Done;
        await Assert.That(group.HeaderIconData).IsEqualTo(ToolCategoryIcons.ForCategory(ToolCategory.Command));
    });
}
```

Add `using Capacitor.App.Views;`.

- [ ] **Step 2: Run — expect fail**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/ToolGroupItemTests/*'
```

Expected: `HeaderIconData` missing.

- [ ] **Step 3: Implement on `ToolGroupItem`**

```csharp
public string HeaderIconData {
    get {
        if (_calls.Count == 1) return ToolCategoryIcons.ForCategory(_calls[0].Category);
        var firstSettled = _calls.FirstOrDefault(c => c.IsSettled);
        return firstSettled is null ? "" : ToolCategoryIcons.ForCategory(firstSettled.Category);
    }
}
```

Raise `nameof(HeaderIconData)` from `RefreshLoneChrome` and `Recompute` (and after `Add` when chrome changes). ViewModels referencing Views is already the pattern for converters in this assembly; if a layering complaint appears, move `ToolCategoryIcons` under `ViewModels/` instead and keep the same API — prefer Views to match `VendorIcons`.

- [ ] **Step 4: Run — expect pass**

Same filter. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/ChatItems.cs test/Capacitor.App.Tests.Unit/ToolGroupItemTests.cs
git commit -m "$(cat <<'EOF'
Expose a tool group's header icon from its category (#1083)

EOF
)"
```

---

### Task 4: Wire `ChatTabView` system cards

**Files:**
- Modify: `src/Capacitor.App/Views/ChatTabView.axaml`

**Interfaces:**
- Consumes: `ChatKindHeader`, `chatSystemCard`, `ToolCategoryIcons`, `ToolGroupItem.HeaderIconData`

- [ ] **Step 1: Update templates**

`SystemNoteItem` — replace the Border attributes with `Classes="systemNote chatSystemCard"` (keep `systemNote` — smoke tests find it). Move `Margin="0,8,0,12"` onto the Border. Replace the Note `TextBlock.toolKindChip` with:

```xml
<views:ChatKindHeader Label="Note" IconData="{x:Static views:ToolCategoryIconsNote}" />
```

`x:Static` cannot call methods — either:
- add `public static string Note => ForFixedLabel("Note");` (and Permission/Question) on `ToolCategoryIcons`, or
- set `IconData` in code-behind (avoid), or
- bind via a markup extension.

**Preferred:** add static properties on `ToolCategoryIcons`:

```csharp
public static string NoteIcon => ForFixedLabel("Note");
public static string PermissionIcon => ForFixedLabel("Permission");
public static string QuestionIcon => ForFixedLabel("Question");
```

Then: `IconData="{x:Static views:ToolCategoryIcons.NoteIcon}"`.

`ToolGroupItem` Border:
- `Classes="toolGroup chatSystemCard"` (+ existing `packsWithCard` / `folded` classes)
- Remove `MaxWidth`, `MinWidth`, `Padding`, `Background`, `BorderBrush`, `BorderThickness`, `HorizontalAlignment="Stretch"` (styles own them)
- Keep toolGroup margin styles in the UserControl.Styles section

Lone kind header — replace the kind-chip `TextBlock` grid with:

```xml
<Grid ColumnDefinitions="*,Auto" IsVisible="{Binding ShowsKindChip}">
    <views:ChatKindHeader Label="{Binding KindChip}" IconData="{Binding HeaderIconData}"
                          Status="{Binding LoneCall}" />
</Grid>
```

Status slot: today status is a Panel of pills bound to `LoneCall`. Either:
- put that Panel as `ChatKindHeader.Status` via a nested DataTemplate / content in XAML — ContentPresenter needs an object; simplest is keep the status Panel in column 1 beside the header (header without Status), matching today’s grid, **or**
- use `Status` with a DataTemplate on the header for `ToolCallItem`.

**Preferred (less magic):** keep the two-column grid — `ChatKindHeader` in col 0 (no Status), existing status `Panel` in col 1 bound to `LoneCall`.

Multi-call summary button — inside the summary `Grid`, before the chevron column (or between chevron and text), add:

```xml
<Panel Width="14" Height="14" VerticalAlignment="Center" Margin="0,-2,0,0"
       IsVisible="{Binding HeaderIconData, Converter={x:Static StringConverters.IsNotNullOrEmpty}}">
    <Path Width="14" Height="14" Stretch="Uniform" Data="{Binding HeaderIconData}"
          Stroke="{StaticResource KcapMutedBrush}" StrokeThickness="1.4"
          StrokeLineCap="Round" StrokeJoin="Round" />
</Panel>
```

Adjust `ColumnDefinitions` to `Auto,Auto,*` (icon, chevron, text) or `Auto,*,Auto` — keep chevron left of text as today; put category icon left of chevron: `Auto,Auto,*`.

User / Assistant: leave as text-only `toolKindChip` (no icon).

- [ ] **Step 2: Build**

```bash
dotnet build src/Capacitor.App/Capacitor.App.csproj
```

Expected: 0 errors. Fix any binding/`x:Static` issues.

- [ ] **Step 3: Commit**

```bash
git add src/Capacitor.App/Views/ChatTabView.axaml src/Capacitor.App/Views/ToolCategoryIcons.cs
git commit -m "$(cat <<'EOF'
Left-align system chat cards and wire kind headers (#1083)

EOF
)"
```

---

### Task 5: Wire prompt cards

**Files:**
- Modify: `src/Capacitor.App/Views/PendingCardTemplates.axaml`
- Modify: `src/Capacitor.App/Views/ChatTabView.axaml` (pending `ContentControl` classes)

**Interfaces:**
- Consumes: `chatPromptCard`, `ChatKindHeader`, `ToolCategoryIcons.PermissionIcon` / `QuestionIcon`

- [ ] **Step 1: Pending host**

In `ChatTabView.axaml` `PendingCardItem` template:

```xml
<ContentControl Classes="pendingCard chatPromptCard"
                Classes.packsWithPrevious="{Binding PacksWithPrevious}"
                Content="{Binding Card}">
```

Remove local `MaxWidth="660" HorizontalAlignment="Stretch"`.

- [ ] **Step 2: Card Borders**

On Permission / Question / AcpQuestion root Borders: `Classes="chatPromptCard"` and drop duplicated Background/Border/Padding/CornerRadius/Margin that the style sets.

Replace:
- Permission chip → `<views:ChatKindHeader Label="Permission" IconData="{x:Static views:ToolCategoryIcons.PermissionIcon}" />`
- ACP `Question` chip → same with `Question` / `QuestionIcon`
- AskUserQuestion step header (`toolKindChip` on `Header`) → `<views:ChatKindHeader Label="{Binding Header}" IconData="{x:Static views:ToolCategoryIcons.QuestionIcon}" IsVisible="{Binding ShowsHeader}" />`  
  (Always Question icon for step headers; label stays the step title.)

Add `xmlns:views` if the resource dictionary lacks it.

- [ ] **Step 3: Build**

```bash
dotnet build src/Capacitor.App/Capacitor.App.csproj
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Capacitor.App/Views/PendingCardTemplates.axaml src/Capacitor.App/Views/ChatTabView.axaml
git commit -m "$(cat <<'EOF'
Center prompt cards and share their kind headers (#1083)

EOF
)"
```

---

### Task 6: Smoke tests for alignment and headers

**Files:**
- Modify: `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs`

**Interfaces:**
- Consumes: rendered `Border.chatSystemCard` / `ContentControl.chatPromptCard`, `ChatKindHeader`

- [ ] **Step 1: Update existing chip test finders if needed**

`Chat_bubbles_carry_a_kind_chip` looks for `TextBlock.toolKindChip`. Labels inside `ChatKindHeader` still use that class — should keep working. If Note moves into the header, still assert `"Note"` in chip texts.

Update `A_single_settled_call_shows_the_row_without_a_summary` to also require a visible `ChatKindHeader`:

```csharp
var header = host.View.GetVisualDescendants().OfType<ChatKindHeader>()
    .Single(h => h.IsEffectivelyVisible);
await Assert.That(header.Label).IsEqualTo("Search");
await Assert.That(header.IconData).IsNotEmpty();
```

(Bash+`ls -la` still classifies as Search in this fixture — keep the existing KindChip expectation.)

- [ ] **Step 2: Add alignment tests**

```csharp
[Test]
[NotInParallel("AvaloniaSession")]
public async Task System_tool_cards_align_left_not_stretch() {
    await RunOnUiAsync(async () => {
        var host = new Host();
        await host.LoadAsync(Tmp.CreateFile("align.jsonl", [ToolCallLine, ToolResultLine]));
        host.Settle();
        var card = host.View.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("toolGroup"));
        await Assert.That(card.HorizontalAlignment).IsEqualTo(Avalonia.Layout.HorizontalAlignment.Left);
        await Assert.That(card.Classes.Contains("chatSystemCard")).IsTrue();
        await host.CloseAsync();
    });
}

[Test]
[NotInParallel("AvaloniaSession")]
public async Task Pending_prompt_host_is_centered() {
    await RunOnUiAsync(async () => {
        var host = new Host();
        await host.LoadAsync(Tmp.CreateFile("prompt-align.jsonl", [
            """{"type":"user","message":{"role":"user","content":"hello"}}""",
        ]));
        host.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Bash"));
        await WaitUntilAsync(() => host.Chat.Items.OfType<PendingCardItem>().Any(), what: "the card");
        host.Settle();
        var hostControl = host.View.GetVisualDescendants().OfType<ContentControl>()
            .Single(c => c.Classes.Contains("pendingCard"));
        await Assert.That(hostControl.HorizontalAlignment).IsEqualTo(Avalonia.Layout.HorizontalAlignment.Center);
        await Assert.That(hostControl.Classes.Contains("chatPromptCard")).IsTrue();
        await host.CloseAsync();
    });
}
```

- [ ] **Step 3: Run smoke filters**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/ChatTabViewSmokeTests/*'
```

Expected: all pass. Fix finders if virtualization hides a header (call `host.Settle()` / scroll as siblings do).

- [ ] **Step 4: Commit**

```bash
git add test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs
git commit -m "$(cat <<'EOF'
Pin left system cards and centered prompt hosts (#1083)

EOF
)"
```

---

### Task 7: CHANGES + sanity

**Files:**
- Modify: `docs/CHANGES.md`

- [ ] **Step 1: Prepend a CHANGES section**

```markdown
## Desktop chat system cards sit left with a shared kind header

Avalonia centers a Stretch child capped by MaxWidth, so Command/Skill tool groups read as
centered against left-aligned notes. System cards (notes and tool groups) share one shell and a
kind header (muted Material-like icon + label) aligned left; permission and question prompts stay
centered on a raised shell. User speech stays right. The transcript model is unchanged — chrome
only.
```

- [ ] **Step 2: Full App unit suite + publish warning grep**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || true
```

Expected: suite green; no new IL3050/IL2026 from this work (App is not the AOT publish target, but run the grep if any shared code moved — skip if only App UI changed).

- [ ] **Step 3: Commit**

```bash
git add docs/CHANGES.md
git commit -m "$(cat <<'EOF'
Record why chat system cards share left kind headers (#1083)

EOF
)"
```

---

## Self-review (plan vs spec)

| Spec decision | Task |
|---|---|
| D1 roles / placement | 4, 5, 6 |
| D2 shared styles + ChatKindHeader | 2, 4, 5 |
| D3 visual grammar (padding/fill/no MinWidth) | 2, 4 |
| D4 icons | 1, 4, 5 |
| D5 label rules (lone / multi / fixed) | 3, 4, 5 |
| D6 consistency, no assistant card, no merge rows | 4 (assistant untouched) |
| Tests | 1, 3, 6 |
| Out of scope respected | no transcript merge, no Mud colours, no assistant border |

No TBD placeholders. Task 6 pending arrange matches `Card_renders_with_its_buttons_and_leaves_the_list_when_empty` (`PermissionEntries.Entry` + `WaitUntilAsync`). Keep `systemNote` class for the existing note smoke finder.
