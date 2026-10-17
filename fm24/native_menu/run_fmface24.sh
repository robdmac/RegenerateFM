#!/bin/sh
# Starts Football Manager 2024 on macOS with FM Face's library
# (libfmface24.dylib), which adds the portrait and Squad menus and serves the
# companion's memory reads.
#
# install.py puts it in the Football Manager 2024 folder, beside fm.app.
# FM24's Steam launch option runs it:
#   "/path/to/Football Manager 2024/run_fmface24.sh" %command%
#
# Nothing in fm.app is changed. FM24's own entitlements allow the library
# (allow-dyld-environment-variables, disable-library-validation).
set -e

BASEDIR=$(cd "$(dirname "$0")" && pwd -P)
GAME="${BASEDIR}/fm.app/Contents/MacOS/fm"

# Steam passes the game (its executable or fm.app) and any game arguments.
if [ $# -gt 0 ]; then
    case "$1" in
        *.app|*.app/) GAME="${1%/}/Contents/MacOS/fm"; shift ;;
        *) if [ -f "$1" ] && [ -x "$1" ]; then GAME="$1"; shift; fi ;;
    esac
fi

if [ ! -x "$GAME" ]; then
    echo "FM Face: Football Manager 2024 isn't at $GAME. Put run_fmface24.sh beside fm.app." >&2
    exit 1
fi

LIBRARY="${BASEDIR}/libfmface24.dylib"
# The library is built for Apple Silicon only; elsewhere FM24 starts without it.
if [ "$(/usr/bin/uname -m)" != "arm64" ] || [ ! -f "$LIBRARY" ]; then
    echo "FM Face: starting FM24 without FM Face (it needs an Apple Silicon Mac and $LIBRARY)." >&2
    exec "$GAME" "$@"
fi

# A library from a downloaded ZIP is quarantined, and macOS won't load a
# quarantined library that isn't notarised.
/usr/bin/xattr -d com.apple.quarantine "$LIBRARY" 2>/dev/null || true

# Steam may already insert its overlay library; keep it. arch is protected
# by System Integrity Protection and drops DYLD_* variables unless they are
# passed with -e; -arm64 runs the native build even from a Rosetta shell.
exec /usr/bin/arch -arm64 \
    -e DYLD_INSERT_LIBRARIES="${LIBRARY}${DYLD_INSERT_LIBRARIES:+:${DYLD_INSERT_LIBRARIES}}" \
    "$GAME" "$@"
