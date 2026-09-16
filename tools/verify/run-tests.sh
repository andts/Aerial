#!/bin/sh
# Compiles and runs the UI-agnostic playback tests under Mono.
#
# The WPF half of the screensaver cannot be built or run on Linux (Mono has no
# PresentationFramework), so PlaybackController deliberately holds all the playlist/failure
# logic and carries no WPF or WinForms types. That is what makes this script possible.
#
# The tests are compiled INTO the same assembly as the sources rather than against a built
# library, because Caching's members are internal.
#
# Usage: tools/verify/run-tests.sh
set -e

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SRC="$ROOT/ScreenSaver"
OUT="${TMPDIR:-/tmp}/aerial-verify"
mkdir -p "$OUT"

echo "Compiling..."
mcs -sdk:4.8 -out:"$OUT/PlaybackTests.exe" \
    -resource:"$SRC/Videos.json",Aerial.Videos.json \
    -r:System.Web.Extensions.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll \
    "$SRC/AerialEntities.cs" \
    "$SRC/Caching.cs" \
    "$SRC/AerialGlobalVars.cs" \
    "$SRC/RegSettings.cs" \
    "$SRC/NativeMethods.cs" \
    "$SRC/PlaybackController.cs" \
    "$ROOT/tools/verify/PlaybackTests.cs"

echo "Running..."
exec mono "$OUT/PlaybackTests.exe"
