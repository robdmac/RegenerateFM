#!/bin/sh
# RegenerateFM on Linux (Steam/Proton): set up FM26, the FM26 Demo and FM24, so
# pressing Play in Steam starts RegenerateFM with them. Quit Steam and close the
# games first. It needs only the system's python3.
#
#   ./install-linux.sh                 every game it finds
#   ./install-linux.sh fm26            one game (fm26, fm26demo or fm24)
#   ./install-linux.sh --remove        uninstall (your faces are kept)
here=$(cd "$(dirname "$0")" && pwd)
if ! command -v python3 >/dev/null 2>&1; then
    echo "RegenerateFM's installer needs python3; install it with your system's package manager." >&2
    exit 1
fi
exec python3 "$here/source/install_linux.py" --release "$here" "$@"
