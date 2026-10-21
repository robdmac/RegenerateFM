#!/bin/sh
# Starts Football Manager 2024 on macOS with RegenerateFM's library
# (libregeneratefm24.dylib), which adds the portrait and Squad menus and serves the
# companion's memory reads.
#
# install.py puts it in the Football Manager 2024 folder, beside fm.app.
# FM24's Steam launch option runs it:
#   "/path/to/Football Manager 2024/run_regeneratefm24.sh" %command%
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
    echo "RegenerateFM: Football Manager 2024 isn't at $GAME. Put run_regeneratefm24.sh beside fm.app." >&2
    exit 1
fi

LIBRARY="${BASEDIR}/libregeneratefm24.dylib"
# The library is built for Apple Silicon only; elsewhere FM24 starts without it.
# Not uname -m: Steam can run under Rosetta, where it says x86_64 on Apple Silicon.
if [ "$(/usr/sbin/sysctl -n hw.optional.arm64 2>/dev/null)" != "1" ] || [ ! -f "$LIBRARY" ]; then
    echo "RegenerateFM: starting FM24 without RegenerateFM (it needs an Apple Silicon Mac and $LIBRARY)." >&2
    exec "$GAME" "$@"
fi

# Start RegenerateFM's companion, as the Windows DLL does: setup_fm24.py records it
# (companion=), the Python to run it with (python=) and that Python's
# architecture (python_arch=; Steam may run this under Rosetta, and the
# packages are built for the architecture setup ran as) in regeneratefm24.ini. It is
# given this shell's process ID, which exec below hands to FM24, and stops
# when FM24 does, or at once if a companion is already running. It logs to
# regeneratefm24-companion.log here.
CONFIG="${BASEDIR}/regeneratefm24.ini"
COMPANION=$(/usr/bin/sed -n 's/^companion=//p' "$CONFIG" 2>/dev/null)
PYTHON=$(/usr/bin/sed -n 's/^python=//p' "$CONFIG" 2>/dev/null)
PYTHON_ARCH=$(/usr/bin/sed -n 's/^python_arch=//p' "$CONFIG" 2>/dev/null)
case "$PYTHON_ARCH" in arm64|x86_64) ;; *) PYTHON_ARCH=arm64 ;; esac
if [ -n "$COMPANION" ] && [ -f "$COMPANION" ] && [ -x "$PYTHON" ]; then
    LOG="${BASEDIR}/regeneratefm24-companion.log"
    if [ -f "$LOG" ] && [ "$(/usr/bin/stat -f %z "$LOG")" -gt 1000000 ]; then
        /bin/mv -f "$LOG" "$LOG.old"
    fi
    # Without Steam's overlay library, which is for the game.
    /usr/bin/nohup /usr/bin/arch -"$PYTHON_ARCH" /usr/bin/env -u DYLD_INSERT_LIBRARIES "$PYTHON" -u "$COMPANION" \
        --started-by-fm24 $$ --game-dir "$BASEDIR" < /dev/null >> "$LOG" 2>&1 &
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
