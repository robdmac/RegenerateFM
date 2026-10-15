"""Read-only FM24 process-memory access probe for Linux/Proton.

This checks access to the running game. It does not decode player records or
write to the process. See process_memory.py for the implementation.
"""

from __future__ import annotations

import argparse

from process_memory import fm24_image_base, read_process_bytes


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", type=int, required=True, help="Running FM24 Wine/Proton process ID")
    args = parser.parse_args()
    try:
        header = read_process_bytes(args.pid, fm24_image_base(args.pid), 2)
    except (OSError, ValueError) as error:
        parser.exit(1, f"FM24 memory access failed: {error}\n")
    if header != b"MZ":
        parser.exit(1, "FM24 memory access failed: unexpected executable header.\n")
    print(f"Read-only FM24 memory access works for PID {args.pid}.")


if __name__ == "__main__":
    main()
