# Patched Avalonia.Native for macOS

`libAvaloniaNative.patched.dylib` is Avalonia's macOS native library, built from the tag the app
references with every `*.patch` here applied, and loaded in place of the package's copy through
`PatchedAvaloniaNative`.

- `automation-leak.patch` releases the COM references the accessibility bridge takes on every
  control an accessibility client touches (AvaloniaUI/Avalonia#8240). Without it, each of those
  controls, its subtree and its DataContext stay alive for the life of the process, which in this
  app means every retired chat tab and PR reader. The upstream rewrite that makes it unnecessary
  (AvaloniaUI/Avalonia#22519) ships in 12.2.
- `metal-resize.patch` is AvaloniaUI/Avalonia#22215, still open upstream. Without it a Metal
  window resized while the render thread is mid-frame keeps drawing into a drawable of the old
  size, which macOS stretches over the new bounds until the next resize (AvaloniaUI/Avalonia#22424).
  A tiling window manager such as AeroSpace resizes every window as it opens, so there it hits
  the main window and Settings on most launches. Drop it once a release we move to carries #22215.

Rebuild after every Avalonia version bump or patch change: `./build.sh`, then commit the new
binary and the version stamp beside it, which `PatchedAvaloniaNativeTests` holds to the
Avalonia.Native assembly the app loads. The script clones the tag, applies the patches, generates
the MicroCom header and runs `xcodebuild`; the resulting file is universal (arm64 and x86_64) and
is signed with the rest of the bundle at release.
