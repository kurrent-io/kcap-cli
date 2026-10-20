# Patched Avalonia.Native for macOS

`libAvaloniaNative.patched.dylib` is Avalonia's macOS native library, built from the tag the app
references with `automation-leak.patch` applied, and loaded in place of the package's copy through
`PatchedAvaloniaNative`.

The patch releases the COM references the accessibility bridge takes on every control an
accessibility client touches (AvaloniaUI/Avalonia#8240). Without it, each of those controls, its
subtree and its DataContext stay alive for the life of the process, which in this app means every
retired chat tab and PR reader. The upstream rewrite that makes the fix unnecessary
(AvaloniaUI/Avalonia#22519) ships in 12.2 and is not backported, so this directory goes away with
the move to Avalonia 12.2.

Rebuild after every Avalonia version bump: `./build.sh`, then commit the new binary. The script
clones the tag, applies the patch, generates the MicroCom header and runs `xcodebuild`; the
resulting file is universal (arm64 and x86_64) and is signed with the rest of the bundle at release.
