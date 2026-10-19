#!/bin/sh
# FM Face for FM24 on Linux (Steam/Proton): set FM24 up so that pressing Play
# in Steam starts FM Face with it. Quit Steam and close FM24 first.
#
# It runs the setup with the system's python3 (no extra packages) for the
# packaged companion beside this script, which FM24 then starts inside its
# Proton prefix. Pass --remove to uninstall.
here=$(cd "$(dirname "$0")" && pwd)
if ! command -v python3 >/dev/null 2>&1; then
    echo "FM Face's setup needs python3; install it with your system's package manager." >&2
    exit 1
fi
exec python3 "$here/../source/fm24/setup_fm24.py" --companion "$here/fmface-fm24.exe" "$@"
