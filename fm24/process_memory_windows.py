"""Read-only Windows access to the running FM24 process (see process_memory).

Uses ReadProcessMemory, VirtualQueryEx and a Toolhelp process snapshot with a
handle opened for querying and reading only. Nothing here attaches to,
pauses, or writes to the game.
"""

from __future__ import annotations

import ctypes
from ctypes import wintypes
from pathlib import Path

CHUNK = 2 * 1024 * 1024
_MIN_REGION = 64

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
MEM_PRIVATE = 0x20000
PAGE_READWRITE = 0x04
PAGE_GUARD = 0x100
TH32CS_SNAPPROCESS = 0x2
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value
LIST_MODULES_ALL = 0x3

_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
_psapi = ctypes.WinDLL("psapi", use_last_error=True)


class _MemoryInfo(ctypes.Structure):
    _fields_ = (("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", wintypes.DWORD), ("PartitionId", wintypes.WORD),
                ("RegionSize", ctypes.c_size_t), ("State", wintypes.DWORD),
                ("Protect", wintypes.DWORD), ("Type", wintypes.DWORD))


class _ProcessEntry(ctypes.Structure):
    _fields_ = (("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
                ("th32ProcessID", wintypes.DWORD), ("th32DefaultHeapID", ctypes.c_void_p),
                ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", ctypes.c_long),
                ("dwFlags", wintypes.DWORD), ("szExeFile", wintypes.WCHAR * 260))


_kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
_kernel32.OpenProcess.restype = wintypes.HANDLE
_kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
_kernel32.ReadProcessMemory.argtypes = (wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                        ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t))
_kernel32.ReadProcessMemory.restype = wintypes.BOOL
_kernel32.VirtualQueryEx.argtypes = (wintypes.HANDLE, ctypes.c_void_p, ctypes.POINTER(_MemoryInfo), ctypes.c_size_t)
_kernel32.VirtualQueryEx.restype = ctypes.c_size_t
_kernel32.CreateToolhelp32Snapshot.argtypes = (wintypes.DWORD, wintypes.DWORD)
_kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
_kernel32.Process32FirstW.argtypes = (wintypes.HANDLE, ctypes.POINTER(_ProcessEntry))
_kernel32.Process32NextW.argtypes = (wintypes.HANDLE, ctypes.POINTER(_ProcessEntry))
_kernel32.QueryFullProcessImageNameW.argtypes = (wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR,
                                                 ctypes.POINTER(wintypes.DWORD))
_psapi.EnumProcessModulesEx.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.HMODULE), wintypes.DWORD,
                                        ctypes.POINTER(wintypes.DWORD), wintypes.DWORD)
_psapi.GetModuleFileNameExW.argtypes = (wintypes.HANDLE, wintypes.HMODULE, wintypes.LPWSTR, wintypes.DWORD)

_handles: dict[int, int] = {}


def _error(what: str) -> OSError:
    code = ctypes.get_last_error()
    return OSError(code, f"{what} failed: {ctypes.FormatError(code).strip()}")


def _handle(pid: int) -> int:
    """A cached query-and-read handle to the game process."""
    handle = _handles.get(pid)
    if handle is None:
        handle = _kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
        if not handle:
            raise _error(f"Opening process {pid}")
        _handles[pid] = handle
    return handle


def _read(pid: int, address: int, size: int) -> bytes:
    """Read up to ``size`` bytes; a partial read returns what was copied."""
    buffer = ctypes.create_string_buffer(size)
    copied = ctypes.c_size_t()
    ok = _kernel32.ReadProcessMemory(_handle(pid), ctypes.c_void_p(address), buffer, size, ctypes.byref(copied))
    if not ok and not copied.value:
        raise _error("ReadProcessMemory")
    return buffer.raw[:copied.value]


def read_process_bytes(pid: int, address: int, size: int) -> bytes:
    """Read a small known address range without attaching or modifying FM."""
    if pid <= 0 or address <= 0 or not 1 <= size <= 4096:
        raise ValueError("Invalid process, address, or read size.")
    data = _read(pid, address, size)
    if len(data) != size:
        raise OSError(f"Partial process read: expected {size} bytes, got {len(data)}.")
    return data


def _main_module(pid: int) -> tuple[int, str]:
    modules = (wintypes.HMODULE * 1)()
    needed = wintypes.DWORD()
    if not _psapi.EnumProcessModulesEx(_handle(pid), modules, ctypes.sizeof(modules), ctypes.byref(needed),
                                       LIST_MODULES_ALL) or not modules[0]:
        raise _error("EnumProcessModulesEx")
    name = ctypes.create_unicode_buffer(1024)
    if not _psapi.GetModuleFileNameExW(_handle(pid), modules[0], name, len(name)):
        raise _error("GetModuleFileNameExW")
    return modules[0], name.value


def fm24_image_base(pid: int) -> int:
    """Find FM24's PE header: the process's main module must be its fm.exe."""
    try:
        base, path = _main_module(pid)
    except OSError as exc:
        raise ValueError(f"Could not read the FM24 executable module: {exc}") from exc
    if not path.replace("\\", "/").casefold().endswith("/football manager 2024/fm.exe"):
        raise ValueError("This process has no mapped FM24 executable header.")
    return base


def process_executable(pid: int) -> Path | None:
    """The FM24 executable this process runs."""
    try:
        _, path = _main_module(pid)
    except OSError:
        return None
    return Path(path) if path.casefold().endswith("\\football manager 2024\\fm.exe") else None


def writable_regions(pid: int):
    """Yield committed private read/write regions, where FM24 keeps its live objects."""
    info = _MemoryInfo()
    address = 0
    while _kernel32.VirtualQueryEx(_handle(pid), ctypes.c_void_p(address), ctypes.byref(info), ctypes.sizeof(info)):
        start = info.BaseAddress or 0
        end = start + info.RegionSize
        if (info.State == MEM_COMMIT and info.Type == MEM_PRIVATE and
                info.Protect & 0xFF == PAGE_READWRITE and not info.Protect & PAGE_GUARD and
                info.RegionSize >= _MIN_REGION):
            yield start, end
        if end <= address:
            break
        address = end


def running_executables():
    """Yield (pid, executable path) for every process this user can query."""
    snapshot = _kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snapshot == INVALID_HANDLE_VALUE:
        raise _error("CreateToolhelp32Snapshot")
    try:
        entry = _ProcessEntry()
        entry.dwSize = ctypes.sizeof(entry)
        more = _kernel32.Process32FirstW(snapshot, ctypes.byref(entry))
        while more:
            pid = entry.th32ProcessID
            path = entry.szExeFile
            handle = _kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
            if handle:
                buffer = ctypes.create_unicode_buffer(1024)
                size = wintypes.DWORD(len(buffer))
                if _kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
                    path = buffer.value
                _kernel32.CloseHandle(handle)
            yield pid, path
            more = _kernel32.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        _kernel32.CloseHandle(snapshot)


class MemoryScan:
    """Iterate over FM24's writable memory in overlapping chunks.

    Each item is ``(address, data)`` where ``address`` is the process address
    of ``data[0]``. The previous chunk's last ``overlap`` bytes are prefixed so
    a needle spanning a chunk boundary is still found. ``complete`` becomes
    false if the game freed or shrank a region during the scan; callers that
    infer absence must check it.
    """

    def __init__(self, pid: int, overlap: int):
        self.pid = pid
        self.overlap = overlap
        self.complete = True
        self.scanned = False
        self._scanning = False

    def read(self, address: int, size: int) -> bytes:
        """Read a larger range than read_process_bytes allows, during a scan."""
        if not self._scanning:
            raise RuntimeError("MemoryScan.read is only available while iterating.")
        try:
            return _read(self.pid, address, size)
        except OSError:
            return b""

    def __iter__(self):
        self._scanning = True
        try:
            for start, end in list(writable_regions(self.pid)):
                address = start
                carried = b""
                while address < end:
                    size = min(CHUNK, end - address)
                    try:
                        chunk = _read(self.pid, address, size)
                    except OSError:
                        self.complete = False
                        break  # The game freed this region during our scan.
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
            self._scanning = False
