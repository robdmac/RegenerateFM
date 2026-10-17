"""Read-only access to the running FM24 process.

Linux/Proton reads through /proc and process_vm_readv; Windows through
ReadProcessMemory and VirtualQueryEx; macOS through FM Face's library inside
FM24, which macOS requires (process_memory_macos). Nothing here attaches to,
pauses, or writes to the game. The backends provide the same functions.
"""

from __future__ import annotations

import sys

if sys.platform == "win32":
    from process_memory_windows import (CHUNK, MemoryScan, fm24_image_base, process_executable,
                                        read_process_bytes, running_executables, writable_regions)
elif sys.platform.startswith("linux"):
    from process_memory_linux import (CHUNK, MemoryScan, fm24_image_base, process_executable,
                                      read_process_bytes, running_executables, writable_regions)
elif sys.platform == "darwin":
    from process_memory_macos import (CHUNK, MemoryScan, fm24_image_base, process_executable,
                                      read_process_bytes, running_executables, writable_regions)
else:
    raise ImportError("FM Face's FM24 support runs on Linux (Steam/Proton), Windows and macOS only.")

__all__ = ["CHUNK", "MemoryScan", "fm24_image_base", "process_executable", "read_process_bytes",
           "running_executables", "writable_regions"]
