# Desktop glass material

Supersedes the material study in PR #833. No GitHub issue exists yet; one is needed
before the implementation PR opens.

## Agreed outcome

The desktop app gets a **material** setting with three values: Opaque, Soft glass and
Liquid glass. It lives in an Appearance card in the Settings window, applies live and
persists. On macOS the default is Soft glass; everywhere else, and for anyone with
macOS "Reduce transparency" switched on, the default is Opaque.

Glass covers the navigation and control layer: the session rail, the launcher's goal
card (`GoalCard`) and chips, and the panel flyouts. Reading surfaces (chat, remote
sessions, the pull request reader) and the Settings, Onboarding and Feedback windows
stay opaque.

The user chose:

- **flyouts in scope** over rail, goal card and chips alone, subject to a render probe;
- **vendored source** over the vendored `.nupkg` or waiting for NuGet.org;
- **a `Surface` control with an inherited material property** over a custom
  `ThemeVariant` or productionising the prototype's attach code;
- **every inline card migrated to `Surface` now** over migrating only the glass sites;
- **Soft glass as the macOS default** over Opaque.

Material is a second axis beside palette. `ThemeVariant` stays pinned to `Dark` and
stays free for a light palette later; nothing here converts `StaticResource` lookups.

## What the code imposes

**Glass is a control, not a brush.** An Avalonia brush paints its own pixels and
cannot sample what is behind it. `LiquidGlassSurface` is a `ContentControl` that
draws a Skia runtime-effect pipeline over a snapshot of the scene. A resource swap
can never turn a `Border` into glass, so views need a seam where the template changes.

**The backdrop snapshot is per top-level window.** `LiquidGlassBackdropProvider` keys
its state on `TopLevel.GetTopLevel(control)`. A flyout in its own native popup window
sees only its own content. `Popup.ShouldUseOverlayLayer` hosts a popup inside the
owning window, at the cost of clipping it to the window bounds.

**The library reflects on a non-public Avalonia member.** `GetRenderer` resolves
`TopLevel.Renderer` with `BindingFlags.NonPublic`. If an Avalonia upgrade renames it,
the lookup returns null and the backdrop stops refreshing with no error.

**The snapshot is the whole window minus excluded visuals.** The capture renderer
walks the top-level's visual tree and skips two things with their subtrees: every
visible `LiquidGlassSurface` in that window, and any visual with
`LiquidGlassBackdrop.IsExcludedFromCapture` set. Anything else drawn in front of a
glass surface is captured, so foreground content that is a sibling of the glass
would be blurred and refracted underneath itself.

**The package is not on NuGet.org.** `LiquidGlassAvaloniaUI` 0.2.0 exists only as a
GitHub release asset. Upstream is MIT, tag `v0.2.0`, commit
`0c65bf0aadc32d50eb0d83215c48f4222f9af84b`.

**`AppStateStore.Read` degrades any deserialization failure to defaults.** An enum
member an older build does not know would throw and reset every other field in
`app-state.json`.

**The repo builds with warnings as errors, enforced code style and a banned-API
analyzer.** Unmodified upstream source does not pass them.

**Glass over the flat canvas refracts nothing.** The material needs coloured content
behind it to read as glass at all.

**The app tests share one non-rendering headless session.** Nothing in the unit suite
can observe shader output.

## Vendored library

Upstream's `LiquidGlassAvaloniaUI/` library directory is copied unmodified to
`src/ThirdParty/LiquidGlassAvaloniaUI/`: 13 C# files, 6 `.sksl` shaders and `LICENSE`.
The demo, browser and test projects are not copied.

- Its own `.csproj` targets `net10.0`. Central package management still applies to
  it, so `Directory.Packages.props` gains `Avalonia.Skia` (12.1.2, with the other
  Avalonia pins) and `SkiaSharp` at the version Avalonia.Skia 12.1.2 resolves. It
  joins `Capacitor.slnx` and is referenced only by `Capacitor.App`.
- A `Directory.Build.props` in `src/ThirdParty/` does not import the root one, which
  takes that subtree out of warnings-as-errors, code-style enforcement and the
  banned-API analyzer. The files stay byte-identical to upstream and an upstream diff
  stays trivial. The vendored `.csproj` states the properties it needs (nullable,
  language version) itself, as upstream's does.
- `src/ThirdParty/LiquidGlassAvaloniaUI/VENDORED.md` records the source URL, tag,
  commit and every local patch with its reason. One patch is planned: the
  pipeline-failure event described under Capability and failure.
- Repo conventions for comments, one type per file and namespaces do not apply inside
  `src/ThirdParty/`. `CLAUDE.md` gains one line saying so and pointing at `VENDORED.md`.
- No `InternalsVisibleTo`.

## Material model

```csharp
public enum SurfaceMaterial { Opaque, SoftGlass, LiquidGlass }
```

**Persistence.** `AppState` gains `string? Material`. The stored values are
`opaque`, `soft_glass` and `liquid_glass`. Parsing is lenient: null, empty or
unrecognised means "no explicit choice". It is a string so that a value this build
does not know leaves the rest of the state file intact.

**Environment.** `MaterialEnvironment(bool GlassCapable, bool ReduceTransparency)` is
built once at startup and injected. `GlassCapable` is `OperatingSystem.IsMacOS()`.
`ReduceTransparency` reads `NSWorkspace.sharedWorkspace
.accessibilityDisplayShouldReduceTransparency` through the same `LibraryImport`
pattern as `AppKitDock`, in a new `AppKitAccessibility`; off macOS it is false. It is
read at startup only. Views and controls never read the platform themselves, so tests
behave the same on every OS.

**State.** Everything a consumer needs is one immutable snapshot:

```csharp
public enum MaterialAvailability { Available, NotCapable, PipelineFailed }

public sealed record MaterialState(
    SurfaceMaterial Effective,
    SurfaceMaterial? Requested,
    MaterialAvailability Availability,
    string? FailureReason,
    bool ReduceTransparency);
```

`Effective` resolves in this order: `NotCapable` → `Opaque`; `PipelineFailed` →
`Opaque`; explicit choice → that choice; `ReduceTransparency` → `Opaque`; otherwise
`SoftGlass`. `FailureReason` is set exactly when `Availability` is `PipelineFailed`.

**Service.** `IMaterialService`:

- `Current` — the latest `MaterialState`.
- `States` — `IObservable<MaterialState>`, replaying `Current` to a new subscriber and
  publishing on every change of any field, including a failure that leaves
  `Effective` where it was.
- `SetAsync(SurfaceMaterial)` — persists through `IAppStateStore.UpdateAsync`, then
  publishes. A failed write still applies for the run, as the store's contract says.
- `ReportPipelineFailure(string reason)` — latches `PipelineFailed` for the session;
  later reports are ignored. See Capability and failure.

An explicit choice of a glass material wins over `ReduceTransparency`: the flag moves
the default, it does not overrule the user.

**Scope.** `MaterialScope.Material` is an inherited attached property of type
`SurfaceMaterial`, default `Opaque`.

- `MainWindow` binds it on `SessionsSurface` to `MainWindowViewModel.Material`, which
  follows `States` and takes `Effective`.
- `WorkspaceHost` sets `MaterialScope.Material="Opaque"` and an explicit
  `KcapCanvasBrush` background, so an open session covers the backdrop.
- No other window sets it, so Settings, Onboarding and Feedback resolve `Opaque`.

Styles select on the inherited value:
`kcap|Surface[(kcap|MaterialScope.Material)=SoftGlass]`.

## Surface

`Surface : ContentControl` in `src/Capacitor.App/Controls/`, with its `ControlTheme`
and material styles in `Controls/Surface.axaml`, included from `App.axaml`.

Class variants:

| Class | Opaque look | Glass |
|---|---|---|
| `card` | `KcapSurfaceBrush`, 1 px `KcapBorderBrush`, radius 12 | yes |
| `raised` | `KcapSurfaceRaisedBrush`, same border | same template as `card` |
| `rail` | today's rail background and border | yes |

Flyout panels are not `Surface`s: the flyout presenters host their own content and
take the glass through `GlassLayer.panel` (see Flyouts).

`CornerRadius` and `Padding` are set per site, as the inline `Border`s set them today.
`BorderBrush` is never set at a site: the resting brush comes from the theme and the
drag-over brush from a style, and a local value would outrank both.

- **Opaque template:** one `Border` with a `ContentPresenter`, drawing `Background`,
  `BorderBrush` and `BorderThickness`. It contains no glass element, so the opaque look
  never touches the shader pipeline and a pipeline failure cannot reach it.
- **Glass template:** a root `Panel` holding a `GlassLayer` and a transparent padded
  `Border` with the `ContentPresenter`. The glass is a background sibling, never a
  wrapper, so content is not reparented and `LiquidGlassSurface.Padding` (which does
  not inset content) is not relied on.
- **Capture boundary:** the glass template's root `Panel` sets
  `LiquidGlassBackdrop.IsExcludedFromCapture="True"`. The whole surface — glass, rim
  and foreground content — is then outside every snapshot of that window, so content
  is never blurred underneath itself. A glass chip inside the goal card samples the
  window backdrop, not the card under it, exactly as in the prototype, where the chips
  sat inside the card's excluded `LiquidGlassSurface`. Every glass template in this
  design (surface, chip, both flyout presenters) sets the flag on its root.
- A material switch re-applies the template around the same `Content` instance.
- Glass colours are named resources (`KcapGlass*`) beside the styles, not inline hex.

### Glass layer

`GlassLayer : TemplatedControl` in `Controls/` is the one place glass is drawn and
parameterised. Its template is a `Panel` with the `LiquidGlassSurface` and a 1 px rim
`Border`; the control is not hit-testable. It has the class variants `card`, `rail`,
`panel` and `chip`, and its styles, keyed on class and `MaterialScope.Material`, own
every glass parameter below. Host templates only place it and pass `CornerRadius` and
the rim brush (`BorderBrush`) through.

Four hosts use it: the `Surface` glass template, the chip template, and the two
flyout presenter templates. They share the layer, never a whole template, because
they present different things: `Surface` and `FlyoutPresenter` a `ContentPresenter`,
`MenuFlyoutPresenter` an `ItemsPresenter`, the chip its own content grid.

Glass parameters, from the prototype:

| | `card` Soft | `card` Liquid | `rail` Soft | `rail` Liquid |
|---|---|---|---|---|
| CornerRadius | 18 | 18 | 22 | 22 |
| BlurRadius | 14 | 5 | 24 | 18 |
| RefractionHeight | 12 | 22 | 18 | 18 |
| RefractionAmount | 5 | 32 | 5 | 14 |
| ChromaticAberration | off | on | off | off |
| Vibrancy | 1.1 | 1.3 | 0.8 | 0.8 |
| TintColor | `#65151D29` | `#35151D29` | `#344E667C` | `#284E667C` |
| SurfaceColor | `#3812151D` | `#1812151D` | `#462F3745` | `#462F3745` |
| HighlightOpacity | 0.35 | 0.7 | 0.28 | 0.42 |
| HighlightWidth | 0.7 | 1.1 | 0.75 | 1 |
| Shadow | `#60000000`, r 28, (0,12) | same | `#65000000`, r 20, (4,8) | same |
| Rim | none | none | `#2EFFFFFF` | `#2EFFFFFF` |

`rail` also sets `HighlightFalloff` 0.65. `panel` starts from the `card` column at
radius 12 and is tuned during the visual check.

### Card migration

Every inline `Border` that is a card becomes a `Surface`. A `Border` is a card when it
carries a Kcap surface brush, a border brush and a corner radius, and holds content.
Pills (radius 999), row highlights and control-template internals stay `Border`s.

About 38 sites across the Onboarding, Settings, pull request reader, pending card,
attachment strip, Home, rail and launcher views. The plan's first migration task
produces the exact inventory. An `x:Name` moves onto the `Surface`. Every migrated
site outside `SessionsSurface` resolves `Opaque` and must render as it does today.

**Attachment drop targets.** `GoalCard` in the launcher and `ComposerCard` in
`ChatTabView` carry `attachTarget`, and `AttachmentDropPaste` toggles `dragOver` on
them. The `Border.attachTarget` selectors in `App.axaml` stop matching a `Surface`,
so they are replaced:

- The resting brush needs no style any more: an opaque `Surface` takes
  `KcapBorderBrush` from its theme, and a glass `card` has no rim, so its resting
  `BorderBrush` is transparent.
- `kcap|Surface.attachTarget.dragOver` sets `BorderBrush` to `KcapPrimaryBrush`. It is
  declared after the `Surface.axaml` include, so it wins over the material styles in
  both materials.
- Both templates draw that brush: the opaque `Border` directly, the glass template
  through the `GlassLayer` rim. Drag-over is therefore visible under glass as a 1 px
  primary rim on a card that otherwise has none.

`HomeViewSmokeTests` and `ChatTabViewSmokeTests` look these cards up as `Border`s;
the lookups change to `Surface` and the behavioural assertions stay.

## Chips

Under a glass scope `Button.kcapChip` takes the prototype's `ControlTemplate`, moved
to `Controls/GlassChipStyles.axaml`: a `GlassLayer.chip`, the content, a focus ring,
`FocusAdorner` nulled, radius 12. Swapping the template is not enough on its own:

- **Opaque fills.** The template keeps the name `PART_ContentPresenter`, which
  `ContentControl` needs to register the presenter, so the four existing
  `Button.kcapChip … /template/ ContentPresenter#PART_ContentPresenter` styles in
  `App.axaml` would still paint opaque fills in the normal, hover, pressed and
  disabled states. Each of them gains `[(kcap|MaterialScope.Material)=Opaque]`. The
  property's default is `Opaque`, so a chip outside any scope still matches.
- **Local values.** The five launcher chips set `Padding="11,5"` and
  `CornerRadius="999"` locally, which outranks any style. Those two attributes move
  into a `Button.kcapChip.picker` style: `11,5` and `999` under `Opaque`, `12,7` under
  glass, where the template's radius 12 applies.
- The dropdown chevron shows only on chips with the new `picker` class. The five
  launcher chips get it.
- Soft: blur 8, refraction 6 (height 8), tint `#16DCEFFF`, surface `#302C3F52`,
  highlight 0.45 / 0.65, vibrancy 1.05, shadow `#40040C16` r 7 (0,2).
- Liquid: blur 3, refraction 12, chromatic aberration on, highlight 0.7 / 0.9.
- Hover: tint `#30DCEFFF`, highlight opacity 0.85. Pressed: surface `#80172533`,
  highlight opacity 0.4, no shadow. `:focus-visible`: 2 px `#B8E9F5` ring.
  Disabled: opacity 0.45.

## Rail

The rail's root becomes `Surface.rail`. Styles keyed on the material set the floating
layout: margin `12,40,12,12` so the panel starts below the macOS window controls,
width 334 (310 opaque), top drag strip 16 (44 opaque). `SessionsSurface` changes its
first column from `310` to `Auto` so the rail owns its width.

Row styles from the prototype's `GlassSidebarStyles.axaml` move to
`Controls/GlassRailStyles.axaml`, keyed on the material instead of the
`glassPrototypeRail` class.

## Backdrop

`MaterialBackdrop : Control` in `Controls/`, declared in `MainWindow.axaml` as the
first child of `SessionsSurface`, spanning both columns, not hit-testable, visible
only when the material is not `Opaque`. The right pane's `Panel` becomes transparent
and clips to bounds.

It draws five radial glows, each fading to transparent, and does not track the
pointer: every move would re-capture the window snapshot and redraw every glass
surface. With `l` the rail width, `w` the remaining width and `h` the height:

| Colour | Centre | Radii |
|---|---|---|
| `#553D7581` | (0.35 l, 0.42 h) | 1.25 l × 0.7 h |
| `#344D4878` | (0.5 l, 0.85 h) | l × 0.45 h |
| `#8023806C` | (l + 0.3 w, 0.55 h) | 0.43 w × 0.46 h |
| `#6851528F` | (l + 0.72 w, 0.59 h) | 0.38 w × 0.4 h |
| `#45226B85` | (l + 0.55 w, 0.35 h) | 0.36 w × 0.33 h |

## Flyouts

Two presenter templates, sharing only the `GlassLayer.panel`:

- `FlyoutPresenter.kcapPanel` — the layer behind a `ContentPresenter`.
- `MenuFlyoutPresenter.kcapPanel` — the layer behind the `ItemsPresenter` in its
  `ScrollViewer`, as Fluent's own template has it, so item presentation and keyboard
  navigation are untouched. The rail's help menu (`RailHelpButton`) is the one real
  `MenuFlyout`; `MainWindowSmokeTests` already drives its three items.

`PopupFlyoutBase.Popup` is public, so one helper sets `Popup.ShouldUseOverlayLayer` on
the seven `kcapPanel` flyouts while their scope is glass; no call site restates the
rule. Under `Opaque` they stay native popups.

**Probe first.** It is its own executable under
`docs/probes/2026-09-18-glass-overlay-flyout/`, a separate process with the Skia
renderer and headless drawing enabled. It cannot live in the unit suite, whose one
per-assembly application is non-rendering and cannot be reconfigured. The window
holds a hard-edged striped backdrop, so blur is measurable, and opens an overlay-layer
`Flyout` and an overlay-layer `MenuFlyout`, both under a glass scope. It passes when,
for both presenter types:

1. the presenter resolves the inherited material through the popup's logical parent;
2. `PipelineUnavailable` was not raised and `LiquidGlassDiagnostics` reports published
   captures;
3. the popup region differs from the same frame rendered with a fully transparent
   presenter, and the stripe edges inside it are measurably softer than outside — a
   draw operation that returned without drawing fails this, because the stripes would
   pass through unchanged;
4. changing the popup's foreground text leaves pixels outside the glyph bounds
   unchanged, which proves the capture boundary keeps content out of its own backdrop.

- **Pass:** both presenters ship with the glass templates.
- **Fail:** flyouts stay opaque in this change and the finding is kept.

## Settings

`SettingsWindow` gets an **Appearance** card above the daemon cards. The "Daemon"
title, its status chip and its subtitle become the heading of the daemon group rather
than of the window.

- Three `RadioButton.kcapChoice`: Opaque, Soft glass, Liquid glass. The selection
  shows `Effective`.
- A choice calls `SetAsync` at once. There is no Save button and no glow toggle.
- `SettingsViewModel` takes `IMaterialService` and renders from `States`, so a window
  opened after a failure shows it. The window itself stays opaque.

| `MaterialState` | Choices | Hint |
|---|---|---|
| `NotCapable` | disabled | glass needs macOS |
| `PipelineFailed` | disabled for the session | glass is off until the next launch, with `FailureReason` |
| `Available`, no explicit choice, `ReduceTransparency` | enabled | Opaque because Reduce transparency is on; picking a glass material overrides it |
| `Available`, otherwise | enabled | none |

A pipeline failure never touches the stored choice, so the next launch tries it again.

## Capability and failure

Glass is offered only where `GlassCapable` is true.

The vendored source reports nothing when its pipeline cannot run: a runtime effect
that fails to compile is written to `Console` and painted as an error hint, and a
missing `ISkiaSharpApiLeaseFeature` makes `Render` return having drawn nothing.
`LiquidGlassDiagnostics` holds capture counters only.

So the library takes one local patch, listed in `VENDORED.md`: a static
`PipelineUnavailable` event on the draw operation, raised once with a reason from
those two paths. It fires on the render thread; the app marshals it to the UI thread
and calls `ReportPipelineFailure`, which drops `Effective` to `Opaque` for the rest of
the session and keeps the stored choice.

Out of scope: reacting to "Reduce transparency" changing while the app runs.

## Not carried over

The branch starts from main, so none of the prototype's scaffolding exists on it and
none is added: `src/Capacitor.App/Prototypes/`, the `--glass-prototype` entry in
`Program.cs`, `scripts/preview-liquid-glass.sh`, the README banner, the `.nupkg` with
its `nuget.config` source mapping, and the `LiquidGlassAvaloniaUI` package version.

The chip template, the rail row styles and the backdrop drawing are ported from
`origin/prototype/desktop-liquid-glass`; that branch is the reference until this
lands.

## Testing

In the existing headless suite, with `MaterialEnvironment` always passed explicitly:

These are structural: the suite's application does not render, so nothing here
asserts pixels. Shader output is the probe's job.

- **Scope:** a `Surface` under a glass scope has a `LiquidGlassSurface` descendant; one
  under a pinned `Opaque` subtree does not; the opaque template never does.
- **Capture boundary:** the root of every glass template (surface, chip, both flyout
  presenters) carries `IsExcludedFromCapture`.
- **Switch:** the same content instance and the goal text survive a material change.
- **Chips:** under `Opaque`, in a scope and outside any, the presenter keeps its opaque
  fill in all four states and the launcher chips keep padding `11,5` and radius 999;
  under glass the presenter has no fill and the padding is `12,7`.
- **Drop targets:** `dragOver` sets the primary brush on `GoalCard` under both
  materials and on the chat `ComposerCard`.
- **Menu flyout:** under glass the help menu still presents its three items.
- **`AppState`:** round trip; missing → no explicit choice; an unknown string → no
  explicit choice with every other field intact.
- **`MaterialService`:** each arm of the `Effective` order, including an explicit glass
  choice beating `ReduceTransparency`; `SetAsync` persists; a failure report latches
  `PipelineFailed` with its reason, drops `Effective` to `Opaque`, keeps `Requested`
  and publishes even when `Effective` did not move; a second report is ignored; a late
  subscriber to `States` gets `Current`; the app-side adapter turns a raised
  `PipelineUnavailable` into exactly one report.
- **Settings view model:** a selection persists and publishes; each row of the Settings
  table, including a view model created after the failure.
- **`MainWindow`:** `WorkspaceHost` resolves `Opaque`; the backdrop is visible only under
  glass; rail width, margin and strip height follow the material.
- **Reflection guard:** `typeof(TopLevel)` still exposes the `Renderer` property the
  vendored provider looks up, so an Avalonia bump fails in CI.
- **Migration:** every migrated view's existing smoke tests stay green.

The final look — three materials in the main window, the flyouts, the Appearance
card — needs a manual check on macOS before merge; the sandbox cannot launch the GUI.

## Delivery

One PR from `alexeyzimarev/desktop-glass-material`, superseding #833, with commits
split as: vendoring; model and service; `Surface` and the card migration; chips, rail
and backdrop; flyouts; settings. `docs/CHANGES.md` gets an entry, and
`README.md` wherever it describes the desktop app's settings.
