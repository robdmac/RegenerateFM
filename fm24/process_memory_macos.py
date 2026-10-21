"""Read-only macOS access to the running FM24 process (see process_memory).

macOS doesn't let another program read FM24's memory: the game is signed with
the hardened runtime and without get-task-allow, so task_for_pid fails even
for root. RegenerateFM's library inside FM24 (native_menu/regeneratefm24_macos.c)
serves reads instead, on loopback TCP at the bridge port + 2, after the same
private token as the click bridge. Its port and token are in regeneratefm24.ini
beside fm.app. Nothing here writes to the game; the library only reads.
"""

from __future__ import annotations

import ctypes
import ctypes.util
import socket
import struct
import threading
from pathlib import Path

CHUNK = 8 * 1024 * 1024
_MIN_REGION = 64
_SUFFIX = "/fm.app/contents/macos/fm"

_libproc = ctypes.CDLL(ctypes.util.find_library("proc") or "/usr/lib/libproc.dylib", use_errno=True)
_libproc.proc_listallpids.argtypes = (ctypes.c_void_p, ctypes.c_int)
_libproc.proc_listallpids.restype = ctypes.c_int
_libproc.proc_pidpath.argtypes = (ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32)
_libproc.proc_pidpath.restype = ctypes.c_int


def running_executables():
    """Yield (pid, executable path) for every process this user can see."""
    count = _libproc.proc_listallpids(None, 0)
    pids = (ctypes.c_int * (count + 64))()
    count = _libproc.proc_listallpids(pids, ctypes.sizeof(pids))
    path = ctypes.create_string_buffer(4096)
    for pid in pids[:max(count, 0)]:
        if pid > 0 and _libproc.proc_pidpath(pid, path, len(path)) > 0:
            yield pid, path.value.decode("utf-8", "replace")


def process_executable(pid: int) -> Path | None:
    """FM24's executable (…/fm.app/Contents/MacOS/fm) if this process runs it."""
    path = ctypes.create_string_buffer(4096)
    if _libproc.proc_pidpath(pid, path, len(path)) <= 0:
        return None
    executable = path.value.decode("utf-8", "replace")
    return Path(executable) if executable.casefold().endswith(_SUFFIX) else None


def _settings(game_dir: Path) -> tuple[int, str]:
    values = {}
    try:
        text = (game_dir / "regeneratefm24.ini").read_text(encoding="ascii")
    except (OSError, UnicodeError) as exc:
        raise OSError(f"RegenerateFM isn't installed in {game_dir} (no regeneratefm24.ini).") from exc
    for line in text.splitlines():
        key, _, value = line.partition("=")
        values[key.strip()] = value.strip()
    try:
        port = int(values["port"])
    except (KeyError, ValueError) as exc:
        raise OSError("regeneratefm24.ini has no valid port.") from exc
    token = values.get("token", "")
    if not 1024 <= port <= 65533 or len(token) < 40:
        raise OSError("regeneratefm24.ini has no valid port or token.")
    return port, token


class _Connection:
    def __init__(self, pid: int):
        executable = process_executable(pid)
        if executable is None:
            raise OSError(f"Process {pid} isn't FM24.")
        port, token = _settings(executable.parents[3])
        self.pid = pid
        self.lock = threading.Lock()
        try:
            self.socket = socket.create_connection(("127.0.0.1", port + 2), timeout=10)
        except OSError as exc:
            raise OSError("FM24 isn't running with RegenerateFM's library (start it with run_regeneratefm24.sh).") from exc
        self.socket.sendall(f"REGENERATEFM24M 1 {token}\n".encode("ascii"))
        if self._exactly(3) != b"OK\n":
            raise OSError("RegenerateFM's library in FM24 refused the connection.")
        self._image: tuple[int, bytes] | None = None

    def _exactly(self, size: int) -> bytes:
        parts = []
        while size:
            part = self.socket.recv(min(size, 4 << 20))
            if not part:
                raise OSError("FM24 closed RegenerateFM's memory connection.")
            parts.append(part)
            size -= len(part)
        return b"".join(parts)

    def image(self) -> tuple[int, bytes]:
        """The image header address and UUID; fixed while this FM24 runs."""
        if self._image is None:
            with self.lock:
                self.socket.sendall(b"B")
                data = self._exactly(24)
            self._image = struct.unpack_from("<Q", data)[0], data[8:]
        return self._image

    def regions(self) -> list[tuple[int, int]]:
        with self.lock:
            self.socket.sendall(b"M")
            count = struct.unpack("<I", self._exactly(4))[0]
            data = self._exactly(16 * count)
        return [struct.unpack_from("<QQ", data, 16 * i) for i in range(count)]

    def read(self, address: int, size: int) -> bytes:
        with self.lock:
            self.socket.sendall(b"R" + struct.pack("<QI", address, size))
            status = struct.unpack("<I", self._exactly(4))[0]
            if status:
                raise OSError(f"FM24 memory at {address:#x} is unreadable (Mach error {status}).")
            return self._exactly(size)


_connections: dict[int, _Connection] = {}
_connections_lock = threading.Lock()


def _connection(pid: int) -> _Connection:
    with _connections_lock:
        connection = _connections.get(pid)
        if connection is None:
            connection = _connections[pid] = _Connection(pid)
        return connection


def _call(pid: int, method: str, *args):
    """Call the connection, reconnecting once if FM24 dropped it."""
    for attempt in range(2):
        connection = _connection(pid)
        try:
            return getattr(connection, method)(*args)
        except (OSError, struct.error) as exc:
            with _connections_lock:
                if _connections.get(pid) is connection:
                    del _connections[pid]
            try:
                connection.socket.close()
            except OSError:
                pass
            if attempt or (isinstance(exc, OSError) and "unreadable" in str(exc)):
                raise


def read_process_bytes(pid: int, address: int, size: int) -> bytes:
    """Read a small known address range without modifying FM."""
    if pid <= 0 or address <= 0 or not 1 <= size <= 4096:
        raise ValueError("Invalid process, address, or read size.")
    return _call(pid, "read", address, size)


def fm24_image_base(pid: int) -> int:
    """The address of FM24's Mach-O header (arm64), from the library inside it."""
    try:
        return _call(pid, "image")[0]
    except OSError as exc:
        raise ValueError(f"No FM24 with RegenerateFM's library in process {pid}: {exc}") from exc


def fm24_image_uuid(pid: int) -> bytes:
    return _call(pid, "image")[1]


def writable_regions(pid: int):
    """Yield read/write mappings not backed by a file, where FM24 keeps its live objects."""
    for start, end in _call(pid, "regions"):
        if end - start >= _MIN_REGION:
            yield start, end


class MemoryScan:
    """Iterate over FM24's writable memory in overlapping chunks (as on Linux).

    Each item is ``(address, data)`` where ``address`` is the process address
    of ``data[0]``. ``complete`` becomes false if the game unmapped a region
    during the scan.
    """

    def __init__(self, pid: int, overlap: int):
        self.pid = pid
        self.overlap = overlap
        self.complete = True
        self.scanned = False
        self._active = False

    def read(self, address: int, size: int) -> bytes:
        """Read a larger range than read_process_bytes allows, during a scan."""
        if not self._active:
            raise RuntimeError("MemoryScan.read is only available while iterating.")
        return _call(self.pid, "read", address, size)

    def __iter__(self):
        self._active = True
        try:
            for start, end in writable_regions(self.pid):
                address = start
                carried = b""
                while address < end:
                    size = min(CHUNK, end - address)
                    try:
                        chunk = _call(self.pid, "read", address, size)
                    except OSError:
                        self.complete = False
                        break  # The game changed this mapping during our scan.
                    self.scanned = True
                    data = carried + chunk
                    yield address - len(carried), data
                    carried = data[-self.overlap:] if self.overlap else b""
                    address += len(chunk)
        finally:
            self._active = False
