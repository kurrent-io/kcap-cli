# LiquidGlassAvaloniaUI

- Source: https://github.com/KaranocaVe/LiquidGlassAvaloniaUI
- Tag: `v0.2.0`
- Commit: `0c65bf0aadc32d50eb0d83215c48f4222f9af84b`
- Licence: MIT, see `LICENSE`
- Copied: the `LiquidGlassAvaloniaUI/` library directory only. The demo, browser and test
  projects are not vendored. The package is not published on NuGet.org.

Every file is byte-identical to upstream except the patches below. This directory is exempt
from the repo's comment, one-type-per-file and namespace rules, and from warnings-as-errors,
through `src/ThirdParty/Directory.Build.props`.

## Local files

- `LiquidGlassAvaloniaUI.csproj` — targets `net10.0` and takes package versions from the
  repo's central pins. Upstream targets `net8.0` on Avalonia 12.0.1.
- `LiquidGlassPipeline.cs` — new. A public `Unavailable` event raised once per process.

## Patches to upstream files

- `LiquidGlassDrawOperation.cs` — upstream reports nothing when the pipeline cannot run: a
  missing Skia lease returns silently and a shader that fails to compile is only written to
  the console. Both paths, and the shader-load `catch`, now also call
  `LiquidGlassPipeline.Report`, so the app can fall back to the opaque material.

## Updating

Diff the new tag's `LiquidGlassAvaloniaUI/` against this directory, re-apply the patches
above, and update the tag and commit here. `VendoredGlassTests` pins the non-public
`TopLevel.Renderer` lookup the backdrop provider depends on.
