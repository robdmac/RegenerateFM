"""Read-only access to the running FM24 process.

Linux/Proton reads through /proc and process_vm_readv; Windows through
ReadProcessMemory and VirtualQueryEx. Nothing here attaches to, pauses, or
writes to the game. Both backends provide the same functions.
"""

from __future__ import annotations

import sys

if sys.platform == "win32":
    from process_memory_windows import (CHUNK, MemoryScan, fm24_image_base, process_executable,
                                        read_process_bytes, running_executables, writable_regions)
elif sys.platform.startswith("linux"):
    from process_memory_linux import (CHUNK, MemoryScan, fm24_image_base, process_executable,
                                      read_process_bytes, running_executables, writable_regions)
else:
    raise ImportError("FM Face's FM24 support runs on Linux (Steam/Proton) and Windows only; "
                      "the macOS build of FM24 is a different program.")

__all__ = ["CHUNK", "MemoryScan", "fm24_image_base", "process_executable", "read_process_bytes",
           "running_executables", "writable_regions"]
