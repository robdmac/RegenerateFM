#!/bin/sh
# RegenerateFM on Linux (Steam/Proton): set up FM27, FM26, the FM26 Demo and FM24, so
# pressing Play in Steam starts RegenerateFM with them. Quit Steam and close the
# games first. Nothing else is needed: linux/regeneratefm is self-contained.
#
#   ./install-linux.sh                 every game it finds
#   ./install-linux.sh fm26            one game (fm27, fm26, fm26demo or fm24)
#   ./install-linux.sh --remove        uninstall (your faces are kept)
here=$(cd "$(dirname "$0")" && pwd)
exec "$here/linux/regeneratefm" install --release "$here" "$@"
