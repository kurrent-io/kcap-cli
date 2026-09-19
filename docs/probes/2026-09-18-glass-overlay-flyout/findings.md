# Glass in overlay-layer flyouts

**Question.** A `LiquidGlassSurface` samples a snapshot of its own top-level window, so a flyout in
its own native popup window sees only itself. Hosting the popup in the owner's window instead puts
it inside that window's snapshot. Does a `kcapPanel` flyout hosted that way then refract the app
behind it?

## Method

`Program.cs` here: Skia renderer, headless drawing off, a window of hard-edged 16 px stripes, a
flyout opened over them and measured from `Window.CaptureRenderedFrame()`. The flyout's popup goes
to the overlay layer under glass (`GlassFlyouts.FollowMaterial`), which the probe asserts through
`Popup.IsUsingOverlayLayer`.

Four checks per arm: the material inherits to the presenter; the glass frame differs from the same
flyout under Opaque across more than half the panel; stripe-edge contrast inside the panel falls
under a quarter of the contrast outside it; and changing the flyout's text changes nothing in the
panel's lower third, which holds no text either way.

Two placements are measured, chosen by the presenter class so both stay runnable:

- **presenter template** (`kcapPanelTemplated`) — a `GlassLayer` inside the presenter's own
  `ControlTemplate`, under a root excluded from the backdrop capture.
- **wrapped content** (`kcapPanel`) — the presenter's `Background` and `BorderBrush` go
  transparent and the content wraps itself in a `Surface` that draws the glass.

Two controls run first, so a failure reads as "the flyout path" rather than "the harness": the same
`GlassLayer Kind="Panel"` over the same stripes as the child of a bare `Popup` with
`ShouldUseOverlayLayer = true`, with and without light dismiss.

Both edge samples take the same pixel row — the panel covers its left, bare stripes its right. A
row picked in window coordinates instead lands under the panel whenever the flyout reaches the
bottom of the window, and the control then measures blurred stripes too.

Only a `Flyout` is measured. No `MenuFlyout` ships: the one menu-shaped flyout, `RailHelpButton`'s
in `SessionRailView.axaml`, is a `Flyout` whose XAML comment records that `MenuFlyout` was rejected
for its Fluent `MenuItem` chrome, so a `MenuFlyoutPresenter` arm would be dead code.

The styles and the two helpers live here, beside the probe, because the app ships none of this.

## Results

```
control overlay popup: region=90, 90, 300, 140 edge outside=10,9 inside=0,0
control overlay popup + light dismiss: region=90, 90, 300, 140 edge outside=10,9 inside=0,0
flyout, presenter template: material=SoftGlass region=0, 173, 300, 120 edge outside=10,3 inside=4,4 vs-opaque=26059px text-ghost=0px
flyout, wrapped content: material=SoftGlass region=0, 173, 302, 122 edge outside=10,4 inside=4,4 vs-opaque=26491px text-ghost=0px
pipeline unavailable: no; captures published: 6
FAIL
```

A bare overlay-layer popup takes stripe contrast from 10.9 to **0.0** — full refraction. Both
flyout placements hold it at **4.4**, against a criterion of a quarter of 10.3. The other three
checks pass on both: the material inherits, the panel differs from its Opaque frame across more
than half its area, and `text-ghost` is 0 px.

## What the failure is

The panel tints but paints no backdrop. Zeroing `TintColor`, `SurfaceColor` and `HighlightOpacity`
on the flyout's own `LiquidGlassSurface` leaves the row at 10.8 against an outside of 10.4,
pixel-for-pixel the raw stripes — so the surface is not under-blurring a backdrop, it has none.
That is the library's `DrawBackdropNotReady` path, taken when the draw operation's snapshot is
null.

Everything the app would control is correct. The presenter's `Background` is `Transparent` under
glass and the opaque token under Opaque; the content carries exactly one `Surface`, one
`GlassLayer` and one `LiquidGlassSurface` under glass and none under Opaque; the material inherits
to the `Surface`; `TopLevel.GetTopLevel(surface)` is the owner window **by reference**;
`LiquidGlassPipeline` never reports unavailable; and the blur filter runs with no surface failures.

## What is ruled out

Each of these is measured, and none changes the reading:

- **Placement** — presenter template and wrapped content fail identically, at the same ratio.
- **The capture exclusion**, cleared on the template root at runtime.
- **Clipping** — the template's `ClipToBounds` removed.
- **Light dismiss**, which a flyout enables and a bare popup does not: a bare popup with it still
  reaches 0.0.
- **Show ordering** — the window shown and pumped before the flyout opens.
- **Snapshot timing** — a slowed pump raising published captures from 6 to 55, byte-identical.
- **An extra frame** — a forced `InvalidateVisual` on the surface plus two further pump rounds,
  byte-identical.
- **The owner `TopLevel`** — the same object as the window, by reference, so the null snapshot is
  not a top-level mix-up.
- **The overlay layer, the vendored pipeline and the Skia harness** — the controls take the same
  layer over the same stripes to 0.0, with `IsUsingOverlayLayer` true throughout.

A reading that puts a `GlassLayer` in the flyout's content while the presenter keeps Fluent's own
template measures 0.0, and is a false positive: Fluent's presenter is opaque, so a glass layer
painting nothing still yields a flat row. The reading only means anything once the presenter is
transparent, and it is then 4.4.

## Conclusion

Glass inside a `Flyout`'s popup never receives a backdrop snapshot and draws through the library's
not-ready path, whichever of the two placements is used, while the identical layer in a bare
overlay-layer `Popup` refracts fully. Flyouts therefore stay opaque: the app includes no flyout
glass styles and no flyout site wraps its content or moves its popup.

The one remaining lead is why a `Flyout`'s popup and a bare `Popup` differ, given the same
`TopLevel`, the same overlay layer and the same layer tree. Settling it means instrumenting
`LiquidGlassBackdropProvider` in a diagnostic build — it is `internal`, under `src/ThirdParty/` —
to log, for each surface, whether a `BackdropState` exists for its top level and whether a snapshot
is published before the frame it draws, then comparing a flyout's popup against a bare popup.
