# Glass in overlay-layer flyouts

**Question.** A `LiquidGlassSurface` samples a snapshot of its own top-level window. Does a
`kcapPanel` flyout, hosted in the overlay layer of the owner's window, refract the app behind it?

**Method.** `Program.cs` here: Skia renderer, headless drawing off, a window of hard-edged 16 px
stripes, a flyout opened over them under a Soft glass scope through the app's own
`GlassFlyoutStyles.axaml`, `SurfaceStyles.axaml` and `GlassFlyouts`. Four checks: the inherited
material, a frame that differs from the same flyout under Opaque, lost stripe-edge contrast inside
the panel, and no change outside the text when the text changes. Controls run first — the same
`GlassLayer Kind="Panel"` over the same stripes as the child of a bare `Popup` with
`ShouldUseOverlayLayer = true`, with and without light dismiss — so a failure reads as "the flyout
path" rather than "the harness".

Only a `Flyout` is measured. No `MenuFlyout` ships: the one menu-shaped flyout, `RailHelpButton`'s
in `SessionRailView.axaml`, is a `Flyout` whose XAML comment records that `MenuFlyout` was rejected
for its Fluent `MenuItem` chrome, so a `MenuFlyoutPresenter` arm would be dead code.

Both edge samples take the same pixel row — the panel covers its left, bare stripes its right. A
row picked in window coordinates instead lands under the panel whenever the flyout reaches the
bottom of the window, and the control then measures blurred stripes too.

**Result — glass layer in the presenter's own `Template`.**

```
Flyout: material=SoftGlass region=0, 173, 324, 143 edge outside=10,8 inside=4,3 vs-transparent=31863px text-ghost=0px
MenuFlyout: material=SoftGlass region=0, 173, 160, 50 edge outside=10,8 inside=4,2 vs-transparent=4687px text-ghost=0px
```

FAIL on the edge check: 4.3 and 4.2 against 10.8 outside, where the criterion is a quarter.

**Result — glass layer in the flyout's CONTENT, presenter transparent.**

```
control overlay popup: region=90, 90, 300, 140 edge outside=10,9 inside=0,0
control overlay popup + light dismiss: region=90, 90, 300, 140 edge outside=10,9 inside=0,0
Flyout: material=SoftGlass region=0, 173, 302, 122 edge outside=10,4 inside=4,4 vs-opaque=26489px text-ghost=0px
pipeline unavailable: no; captures published: 4
```

FAIL on the same check, at the same ratio: 4.4 against 10.4.

**What the failure is.** The panel tints but paints no backdrop. Zeroing `TintColor`,
`SurfaceColor` and `HighlightOpacity` on the flyout's own `LiquidGlassSurface` leaves the stripes
at full contrast — `inside=10.8` against `outside=10.4`, pixel-for-pixel the raw stripes — so the
surface is not under-blurring a backdrop, it has none. That is the library's
`DrawBackdropNotReady` path, taken when the draw operation's snapshot is null.

Everything the app controls is correct under the content shape: the presenter's `Background` is
`Transparent` under glass and `#ff12151d` under Opaque; the content carries exactly one `Surface`
(class `panel`, `GlassKind=Panel`, `GlassCornerRadius=12`), one `GlassLayer` and one
`LiquidGlassSurface` under glass and none under Opaque; the material inherits to the `Surface`;
and `TopLevel.GetTopLevel(surface)` is the owner window by reference. `LiquidGlassPipeline`
never reports unavailable and the blur filter runs without failures.

**What is ruled out.** Each of these was run, and none changes the reading:

- Placement: presenter `Template` and flyout content fail identically, at the same ratio.
- The capture exclusion, cleared on the template root at runtime.
- The template's `ClipToBounds`.
- Show ordering — the window shown and pumped before the flyout opens.
- Frame and snapshot timing: a forced `InvalidateVisual` plus two further pump rounds, and a
  slowed pump that raised published captures from 6 to 55, both byte-identical.
- Light dismiss, which a flyout enables and a bare popup does not.
- The overlay layer itself, the vendored pipeline and the Skia harness: the controls above take
  the same layer over the same stripes to 0.0 in a bare overlay-layer popup, `IsUsingOverlayLayer`
  true in every case.

An earlier reading put a `GlassLayer` in the flyout's content while the presenter kept Fluent's
own template and measured 0.0. That is a false positive and is why the content shape was tried:
Fluent's presenter is opaque, so a glass layer painting nothing still yields a flat row. The
reading only becomes meaningful once the presenter is transparent, and it is then 4.4.

**FAIL** — flyouts stay opaque: `GlassFlyoutStyles.axaml` is not included from `App.axaml` and no
flyout site wraps its content or calls `GlassFlyouts.FollowMaterial`, until glass inside a
flyout's popup can be shown to receive a backdrop snapshot. The one lead left is why a bare
overlay-layer `Popup` and a `Flyout`'s popup differ, which needs instrumenting
`LiquidGlassBackdropProvider` — `internal`, and under `src/ThirdParty/`.
