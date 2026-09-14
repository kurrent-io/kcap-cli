#!/usr/bin/env bash
set -euo pipefail

prototype_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet build "$prototype_root/src/Capacitor.App/Capacitor.App.csproj" -c Debug --nologo
exec dotnet "$prototype_root/src/Capacitor.App/bin/Debug/net10.0/Kurrent Capacitor.dll" --glass-prototype
