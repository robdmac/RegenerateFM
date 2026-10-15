"""Read-only Linux access to the running FM24 Proton process.

Uses the documented process_vm_readv system call and /proc/<pid>/mem. Nothing
here attaches to, pauses, or writes to the game.
"""

from __future__ import annotations

import ctypes
import ctypes.util
import os
from pathlib import Path

CHUNK = 2 * 1024 * 1024
_MIN_REGION = 64


class _IOVec(ctypes.Structure):
    _fields_ = (("base", ctypes.c_void_p), ("length", ctypes.c_size_t))


_libc = ctypes.CDLL(ctypes.util.find_library("c"), use_errno=True)
_readv = _libc.process_vm_readv
_readv.argtypes = (ctypes.c_int, ctypes.POINTER(_IOVec), ctypes.c_ulong,
                   ctypes.POINTER(_IOVec), ctypes.c_ulong, ctypes.c_ulong)
_readv.restype = ctypes.c_ssize_t


def read_process_bytes(pid: int, address: int, size: int) -> bytes:
    """Read a small known address range without attaching or modifying FM."""
    if pid <= 0 or address <= 0 or not 1 <= size <= 4096:
        raise ValueError("Invalid process, address, or read size.")
    buffer = ctypes.create_string_buffer(size)
    local = _IOVec(ctypes.cast(buffer, ctypes.c_void_p), size)
    remote = _IOVec(address, size)
    count = _readv(pid, ctypes.byref(local), 1, ctypes.byref(remote), 1, 0)
    if count < 0:
        code = ctypes.get_errno()
        raise OSError(code, os.strerror(code))
    if count != size:
        raise OSError(f"Partial process read: expected {size} bytes, got {count}.")
    return buffer.raw


def fm24_image_base(pid: int) -> int:
    """Find FM24's mapped PE header, independent of the installation path."""
    for line in Path(f"/proc/{pid}/maps").read_text().splitlines():
        fields = line.split(maxsplit=5)
        if len(fields) != 6 or not fields[1].startswith("r") or fields[2] != "00000000":
            continue
        path = fields[5].replace("\\", "/").casefold()
        if path.endswith("/football manager 2024/fm.exe"):
            return int(fields[0].split("-", 1)[0], 16)
    raise ValueError("This process has no mapped FM24 executable header.")


def writable_regions(pid: int):
    """Yield anonymous read/write mappings, where FM24 keeps its live objects."""
    for line in Path(f"/proc/{pid}/maps").read_text().splitlines():
        parts = line.split(maxsplit=5)
        if len(parts) < 5 or parts[1][:2] != "rw" or parts[4] != "0":
            continue
        if len(parts) == 6 and parts[5] and not parts[5].startswith("["):
            continue
        start, end = (int(value, 16) for value in parts[0].split("-", 1))
        if end - start >= _MIN_REGION:
            yield start, end


class MemoryScan:
    """Iterate over FM24's writable memory in overlapping chunks.

    Each item is ``(address, data)`` where ``address`` is the process address
    of ``data[0]``. The previous chunk's last ``overlap`` bytes are prefixed so
    a needle spanning a chunk boundary is still found. ``complete`` becomes
    false if the game unmapped or shrank a region during the scan; callers that
    infer absence must check it.
    """

    def __init__(self, pid: int, overlap: int):
        self.pid = pid
        self.overlap = overlap
        self.complete = True
        self.scanned = False
        self._memory = None

    def read(self, address: int, size: int) -> bytes:
        """Read a larger range than process_vm_readv allows, during a scan."""
        if self._memory is None:
            raise RuntimeError("MemoryScan.read is only available while iterating.")
        return os.pread(self._memory.fileno(), size, address)

    def __iter__(self):
        with open(f"/proc/{self.pid}/mem", "rb", buffering=0) as memory:
            self._memory = memory
            try:
                for start, end in writable_regions(self.pid):
                    address = start
                    carried = b""
                    while address < end:
                        size = min(CHUNK, end - address)
                        try:
                            chunk = os.pread(memory.fileno(), size, address)
                        except OSError:
                            self.complete = False
                            break  # The game changed this mapping during our scan.
                        if len(chunk) != size:
                            self.complete = False
                            if not chunk:
                                break
                        self.scanned = True
                        data = carried + chunk
                        yield address - len(carried), data
                        carried = data[-self.overlap:] if self.overlap else b""
                        address += len(chunk)
            finally:
                self._memory = None
