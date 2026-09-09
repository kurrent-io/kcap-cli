# Liquid glass material study

Throwaway branch: `prototype/desktop-liquid-glass`.

Question: does LiquidGlassAvaloniaUI make the existing Capacitor launcher feel better?
The layout, controls and typography stay consistent so the material can be judged directly.

From the worktree root:

```sh
bash scripts/preview-liquid-glass.sh
```

The preview opens the real MainWindow, session rail and launcher with three sample sessions.
Data and picker preferences live in memory. Start displays a preview message. No daemon,
server connection, updater or tray is started. Close the window to quit.

- **Current:** the existing opaque composer.
- **Soft glass:** 14 DIP blur, 5 DIP refraction, muted tint and restrained edge lighting.
- **Liquid glass:** 5 DIP blur, 32 DIP refraction, stronger highlights and colour separation.
- **Backdrop glow:** compare each material over either a coloured glow or the original flat canvas.

The repository, machine, harness, effort and permission chips follow the selected material.
Glass chips have translucent surfaces, edge lighting, dropdown chevrons and explicit hover,
pressed and keyboard-focus states. Current restores the original chip templates and spacing.

The sidebar also follows the material: glass modes use an inset panel with 22 DIP corners,
frosted tint, a light rim and a shared backdrop extending behind the session tree. The panel
starts below the macOS window controls. Current restores the original full-height sidebar.

Move the pointer inside the launcher to shift the glow slightly and see the live backdrop.
Use the buttons or left/right arrows to switch; text fields retain their usual arrow-key behaviour.
The entry point is available only in Debug builds with `--glass-prototype`.

## Dependency provenance

NuGet.org did not resolve this package on 2026-09-09. The original release package is retained
in `packages/`, with an exact package-source mapping in the worktree's `nuget.config`.

- Source: https://github.com/KaranocaVe/LiquidGlassAvaloniaUI/releases/tag/v0.2.0
- Package: `LiquidGlassAvaloniaUI.0.2.0.nupkg`
- License: MIT
- Upstream commit: `0c65bf0aadc32d50eb0d83215c48f4222f9af84b`
- SHA-256: `841a865c68397f169099562d66f1382956cb8a7fabe039962a734c10073c7bf7`
- Checksum verified against the release's `SHA256SUMS.txt`.

Restore resolves Avalonia/Avalonia.Skia 12.1.2 and stable SkiaSharp 3.119.4.
The desktop project fully rebuilds with zero errors and zero warnings on macOS arm64.
Native visual checks covered all three materials, glow on/off, text preservation across
material switches, and the preview-only Start response. The glass content needs an inner
Border for padding: setting LiquidGlassSurface.Padding alone did not inset the controls.
The chip check also covered keyboard focus, selecting an effort through its picker, and
preserving that selection when restoring the original material.
The floating sidebar was visually checked with its footer visible; expanding/collapsing
session groups works and the collapse state survives switching to Current and back.
This is a visual experiment; cross-platform rendering, sustained scrolling performance,
accessibility fallback and production integration remain unvalidated. The user's material
choice is pending; nothing from this experiment has been promoted to main.
