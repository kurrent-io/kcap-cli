#!/usr/bin/env bash
# Builds libAvaloniaNative.patched.dylib next to this script: Avalonia's macOS native library at
# the tag Directory.Packages.props references, with every *.patch beside this script applied, as a
# universal arm64 + x86_64 binary. Needs Xcode, the .NET SDK and network access for the clone.
# Usage: build.sh [work-dir]   (a fresh directory each run; the default is a temporary one)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
props="$here/../../../../Directory.Packages.props"
tag="$(sed -n 's/.*<PackageVersion Include="Avalonia" Version="\([^"]*\)".*/\1/p' "$props")"
[ -n "$tag" ] || { echo "no Avalonia version in $props" >&2; exit 1; }
work="${1:-$(mktemp -d)}"
src="$work/Avalonia"
out="$work/out"
echo "Avalonia $tag -> $src"
git clone --quiet --filter=blob:none --sparse --depth 1 --branch "$tag" https://github.com/AvaloniaUI/Avalonia.git "$src"
git -C "$src" sparse-checkout set native src/Avalonia.Native nukebuild build
for patch in "$here"/*.patch; do git -C "$src" apply --whitespace=nowarn "$patch"; done
# Generates native/Avalonia.Native/inc/avalonia-native.h from the IDL the managed side was built against.
dotnet build "$src/native/Avalonia.Native/Avalonia.Native.macOS.proj" -t:GenerateMicroComItems -nologo -v q
# The project file pins a deployment target current Xcode no longer accepts; 12.0 is well under the
# app's own floor.
xcodebuild -project "$src/native/Avalonia.Native/src/OSX/Avalonia.Native.OSX.xcodeproj" -configuration Release \
  CONFIGURATION_BUILD_DIR="$out" ONLY_ACTIVE_ARCH=NO ARCHS="arm64 x86_64" MACOSX_DEPLOYMENT_TARGET=12.0 -quiet
cp "$out/libAvalonia.Native.OSX.dylib" "$here/libAvaloniaNative.patched.dylib"
# PatchedAvaloniaNativeTests holds this to the Avalonia.Native assembly the app loads.
printf '%s\n' "$tag" > "$here/libAvaloniaNative.patched.version"
file "$here/libAvaloniaNative.patched.dylib"
shasum -a 256 "$here/libAvaloniaNative.patched.dylib"
