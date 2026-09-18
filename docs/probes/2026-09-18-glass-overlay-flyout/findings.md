# Glass in overlay-layer flyouts

**Question.** A `LiquidGlassSurface` samples a snapshot of its own top-level window. Does a
`kcapPanel` flyout, hosted in the overlay layer of the owner's window, refract the app behind
it — for a `Flyout` and for a `MenuFlyout`?

**Method.** `Program.cs` here: Skia renderer, headless drawing off, a window of hard-edged
16 px stripes, each flyout opened under a Soft glass scope through the app's own
`GlassFlyoutStyles.axaml` and `GlassFlyouts.FollowMaterial`. Four checks per presenter type:
the inherited material, a frame that differs from the same flyout with its glass layer hidden,
lost stripe-edge contrast inside the panel, and no change outside the text when the text changes.

Two controls run first, so a failure is readable as "the flyout path" rather than "the harness":
the same `GlassLayer Kind="Panel"` over the same stripes, once placed in the window and once as
the child of a bare `Popup` with `ShouldUseOverlayLayer = true`.

Both edge samples take the same pixel row — the panel covers its left, bare stripes its right.
A row picked in window coordinates instead lands under the panel whenever the flyout reaches
the bottom of the window, and the control then measures blurred stripes too. The menu items
carry a fixed width for the same reason: a menu sized by its own text changes width when the
text changes, and the text-ghost check would measure that shift rather than a ghost.

**Result.**

```
control in-window: region=90, 90, 300, 140 edge outside=10,9 inside=0,0
control overlay popup: region=90, 90, 300, 140 edge outside=10,9 inside=0,0
Flyout: material=SoftGlass region=0, 173, 324, 143 edge outside=10,8 inside=4,3 vs-transparent=31863px text-ghost=0px
MenuFlyout: material=SoftGlass region=0, 173, 160, 50 edge outside=10,8 inside=4,2 vs-transparent=4687px text-ghost=0px
pipeline unavailable: no; captures published: 8
```

The material inherits, the capture exclusion holds (`text-ghost=0px` on both), and the layer
draws (`vs-transparent` is over half each region). The edge check is what fails: stripe contrast
inside the panel is 4.3 and 4.2 against 10.8 outside, where the criterion is a quarter. The
stripes under the panel stay pixel-sharp and land on exactly the edges they occupy in the frame
with the layer hidden — the panel tints, it does not refract. Stripping tint, surface and
highlight from the flyout's surface leaves the stripes at full 10.6 contrast, so the surface is
painting no backdrop at all rather than an under-blurred one. The same layer over the same
stripes reaches 0.0 in the window and in a bare overlay-layer popup, so neither the vendored
pipeline, the Skia harness, nor the overlay layer is the cause; a `GlassLayer` placed in the
flyout's content under Fluent's own presenter template also fails once the `kcapPanel` glass
template is applied to the presenter. Neither the capture exclusion, the template's
`ClipToBounds`, showing the window before the flyout, forcing a re-invalidation, nor a slower
pump (55 published captures) changes the reading.

**FAIL** — flyouts stay opaque in this change: `GlassFlyoutStyles.axaml` is not included from
`App.axaml` and no flyout site calls `GlassFlyouts.FollowMaterial`, until the flyout presenter's
glass layer can be shown to receive a backdrop snapshot.
