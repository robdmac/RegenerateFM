#!/bin/sh
# Starts Football Manager 26 (or the FM26 Demo) on macOS with BepInEx and the
# FM Face plugin.
#
# It lives in the game folder, beside fm.app, with the rest of the release's
# FM26-macOS folder. The game's Steam launch option runs it:
#   "/path/to/Football Manager 26/run_fmface.sh" %command%
#
# BepInEx's macOS runtime (CoreCLR and its native helpers) is x86_64 only, so
# the game runs under Rosetta 2. FM26 is a universal app, so nothing in fm.app
# is changed.
set -e

BASEDIR=$(cd "$(dirname "$0")" && pwd -P)
# The app's executable: "Football Manager 26", or "Football Manager 26 Demo".
executable() {
    /usr/libexec/PlistBuddy -c "Print :CFBundleExecutable" "$1/Contents/Info.plist" 2>/dev/null ||
        echo "Football Manager 26"
}
GAME="${BASEDIR}/fm.app/Contents/MacOS/$(executable "${BASEDIR}/fm.app")"

# Steam passes the game (its executable or fm.app) and any game arguments.
if [ $# -gt 0 ]; then
    case "$1" in
        *.app|*.app/) GAME="${1%/}/Contents/MacOS/$(executable "${1%/}")"; shift ;;
        *) if [ -f "$1" ] && [ -x "$1" ]; then GAME="$1"; shift; fi ;;
    esac
fi

if [ ! -x "$GAME" ]; then
    echo "FM Face: Football Manager 26 isn't at $GAME. Put run_fmface.sh beside fm.app." >&2
    exit 1
fi
if ! /usr/bin/arch -x86_64 /usr/bin/true 2>/dev/null; then
    echo "FM Face needs Rosetta 2. Install it with: softwareupdate --install-rosetta" >&2
    exit 1
fi

# Files from a downloaded ZIP are quarantined, and macOS refuses to load a
# quarantined library that isn't notarised ("libdoorstop.dylib" Not Opened),
# which leaves FM26 waiting on that dialog. Clear it from the files this
# folder adds; fm.app is left alone.
for installed in "${BASEDIR}/libdoorstop.dylib" "${BASEDIR}/dotnet" "${BASEDIR}/BepInEx"; do
    /usr/bin/xattr -r -d com.apple.quarantine "$installed" 2>/dev/null || true
done

# BepInEx looks for the game's Data folder beside fm.app; on macOS it is in
# the app bundle.
if [ ! -e "${BASEDIR}/Data" ]; then
    ln -s "fm.app/Contents/Resources/Data" "${BASEDIR}/Data"
fi

export DOORSTOP_ENABLED=1
export DOORSTOP_TARGET_ASSEMBLY="${BASEDIR}/BepInEx/core/BepInEx.Unity.IL2CPP.dll"
export DOORSTOP_BOOT_CONFIG_OVERRIDE=
export DOORSTOP_IGNORE_DISABLED_ENV=0
export DOORSTOP_CLR_RUNTIME_CORECLR_PATH="${BASEDIR}/dotnet/libcoreclr.dylib"
export DOORSTOP_CLR_CORLIB_DIR="${BASEDIR}/dotnet"
export BEPINEX_GAME_ASSEMBLY_PATH="${BASEDIR}/fm.app/Contents/Frameworks/GameAssembly.dylib"
export BEPINEX_PRELOADER_LOG="${BASEDIR}/BepInEx/preloader-error.log"

# arch is protected by System Integrity Protection, so it drops DYLD_*
# variables unless they are passed with -e. Steam may already insert its
# overlay library; keep it.
exec /usr/bin/arch -x86_64 \
    -e DYLD_INSERT_LIBRARIES="${BASEDIR}/libdoorstop.dylib${DYLD_INSERT_LIBRARIES:+:${DYLD_INSERT_LIBRARIES}}" \
    -e DYLD_LIBRARY_PATH="${BASEDIR}/fm.app/Contents/Frameworks" \
    "$GAME" "$@"
