"""Read files from FM24's .fmf game archives (read-only).

Layout of the validated 24.4.2 archives: byte 9 holds the offset of a
zstd-compressed index near the end of the file. The index is a directory
tree; each file entry gives the offset of a 30-byte local header followed
by the file's data, zstd-compressed or stored as-is.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path

import zstandard

ZSTD_MAGIC = b"\x28\xb5\x2f\xfd"
LOCAL_HEADER = 30
MAX_INDEX = 64 << 20
MAX_FILE = 32 << 20


@dataclass(frozen=True)
class Entry:
    path: str
    offset: int
    compressed: int
    size: int


def _decompress(data: bytes, limit: int) -> bytes:
    # Large data is stored as frames of up to 128 KiB each, every frame
    # after the first preceded by its 32-bit compressed length.
    out = bytearray()
    while len(out) <= limit:
        if data[4:8] == ZSTD_MAGIC and out:
            data = data[4:]
        if not data.startswith(ZSTD_MAGIC):
            break
        frame = zstandard.ZstdDecompressor().decompressobj()
        out += frame.decompress(data)
        if not frame.eof:
            raise ValueError("FM24 archive data is truncated.")
        data = frame.unused_data
    if len(out) > limit:
        raise ValueError("FM24 archive data is larger than expected.")
    return bytes(out)


def read_index(archive: Path) -> dict[str, Entry]:
    """Every file in the archive by lower-case path ("/graphics/...")."""
    with open(archive, "rb") as handle:
        header = handle.read(17)
        if len(header) != 17 or header[2:6] != b"fmf.":
            raise ValueError(f"Not an FM archive: {archive}")
        handle.seek(struct.unpack_from("<Q", header, 9)[0])
        tail = handle.read(MAX_INDEX)
    start = tail.find(ZSTD_MAGIC, 0, 64)
    if start < 0:
        raise ValueError(f"FM archive index was not found: {archive}")
    data = _decompress(tail[start:], MAX_INDEX)
    entries: dict[str, Entry] = {}
    position = 0

    def u32() -> int:
        nonlocal position
        value = struct.unpack_from("<I", data, position)[0]
        position += 4
        return value

    def text() -> str:
        nonlocal position
        length = u32()
        if length > 4096:
            raise ValueError("FM archive index has an invalid name.")
        value = data[position:position + length].decode("utf-8")
        position += length
        return value

    def directory(parent: str, depth: int) -> None:
        nonlocal position
        if depth > 64:
            raise ValueError("FM archive index is nested too deeply.")
        path = parent + "/" + text()
        for _ in range(u32()):
            name = text() + text()
            offset, compressed, size = struct.unpack_from("<3Q", data, position)
            position += 40
            entry = Entry(path + "/" + name, offset, compressed, size)
            entries[entry.path.casefold()] = entry
        for _ in range(u32()):
            directory(path, depth + 1)

    try:
        directory("", 0)
    except (struct.error, UnicodeDecodeError) as exc:
        raise ValueError(f"FM archive index could not be read: {archive}") from exc
    return entries


def read_file(archive: Path, entry: Entry) -> bytes:
    if entry.size > MAX_FILE or entry.compressed > MAX_FILE:
        raise ValueError(f"{entry.path} is too large to read.")
    with open(archive, "rb") as handle:
        handle.seek(entry.offset + LOCAL_HEADER)
        data = handle.read(entry.compressed)
    if len(data) != entry.compressed:
        raise ValueError(f"{entry.path} is truncated in the archive.")
    if data.startswith(ZSTD_MAGIC):
        data = _decompress(data, entry.size)
    if len(data) != entry.size:
        raise ValueError(f"{entry.path} has an unexpected size in the archive.")
    return data
