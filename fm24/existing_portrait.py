"""Find the portrait FM24 currently uses for a person, for "Update face".

FM24 maps pictures with config.xml files: each record maps a file stem in the
same folder to "graphics/pictures/person/<id>/portrait". Pictures in the
user's graphics folder replace the game's bundled ones, and FM Face's own
folder is checked first. Bundled squad photos are a JPEG plus an optional
"<name>_[mask].png" whose values become the alpha channel.
"""

from __future__ import annotations

import io
import os
import threading
import time
import xml.etree.ElementTree as ET
from pathlib import Path

from PIL import Image

import game_archive
from process_memory import process_executable
from fmface import validate_id

IMAGE_SUFFIXES = (".png", ".jpg", ".jpeg")
MAX_CONFIG = 8 << 20
MAX_IMAGE = 16 << 20
_bundled_lock = threading.Lock()
_bundled: dict[tuple[str, int], tuple[dict[str, game_archive.Entry], dict[str, str]]] = {}


def _targets(person_id: str) -> set[str]:
    return {f"graphics/pictures/person/{prefix}{person_id}/portrait" for prefix in ("", "r-")}


def _stems(config: bytes, targets: set[str]) -> list[str]:
    try:
        root = ET.fromstring(config)
    except ET.ParseError:
        return []
    return [record.get("from", "") for record in root.iter("record")
            if (record.get("to") or "").strip().replace("\\", "/").casefold() in targets
            and record.get("from")]


def _rgba_png(image: bytes, mask: bytes | None) -> bytes:
    with Image.open(io.BytesIO(image)) as picture:
        picture = picture.convert("RGBA")
        if mask is not None:
            with Image.open(io.BytesIO(mask)) as alpha:
                alpha = alpha.getchannel("A") if alpha.mode == "RGBA" else alpha.convert("L")
                if alpha.size != picture.size:
                    raise ValueError("The portrait mask does not match its photo.")
                picture.putalpha(alpha)
        output = io.BytesIO()
        picture.save(output, "PNG")
        return output.getvalue()


def _user_portrait(graphics_root: Path, preferred: Path, targets: set[str]) -> tuple[bytes, str] | None:
    configs = [preferred / "config.xml"]
    for folder, directories, files in os.walk(graphics_root):
        directories.sort()
        if "config.xml" in files and Path(folder) != preferred:
            configs.append(Path(folder) / "config.xml")
    for config in configs:
        try:
            if not config.is_file() or config.stat().st_size > MAX_CONFIG:
                continue
            stems = _stems(config.read_bytes(), targets)
        except OSError:
            continue
        for stem in stems:
            for suffix in IMAGE_SUFFIXES:
                path = config.parent / (stem + suffix)
                if path.is_file() and path.stat().st_size <= MAX_IMAGE:
                    mask = config.parent / (stem + "_[mask].png")
                    return _rgba_png(path.read_bytes(), mask.read_bytes() if mask.is_file() else None), str(path)
    return None


def _bundled_index(archive: Path) -> tuple[dict[str, game_archive.Entry], dict[str, str]]:
    """The archive's files, and the bundled picture stem of each mapped person.

    Built once per archive version (about half a second)."""
    key = (str(archive), archive.stat().st_mtime_ns)
    with _bundled_lock:
        if key in _bundled:
            return _bundled[key]
        entries = game_archive.read_index(archive)
        mapped: dict[str, str] = {}
        for path, entry in entries.items():
            if not path.startswith("/graphics/pictures/") or not path.endswith("/config.xml"):
                continue
            folder = path[:-len("config.xml")]
            try:
                root = ET.fromstring(game_archive.read_file(archive, entry))
            except (ET.ParseError, ValueError):
                continue
            for record in root.iter("record"):
                target = (record.get("to") or "").strip().casefold()
                stem = record.get("from")
                prefix = "graphics/pictures/person/"
                if stem and target.startswith(prefix) and target.endswith("/portrait"):
                    person = target[len(prefix):-len("/portrait")].removeprefix("r-")
                    mapped.setdefault(person, folder + stem.casefold())
        _bundled.clear()
        _bundled[key] = entries, mapped
        return entries, mapped


def _bundled_portrait(game_dir: Path, person_id: str) -> tuple[bytes, str] | None:
    archive = game_dir / "data" / "game" / "graphics.fmf"
    if not archive.is_file():
        return None
    entries, mapped = _bundled_index(archive)
    stem = mapped.get(person_id)
    if stem is None:
        return None
    for suffix in IMAGE_SUFFIXES:
        entry = entries.get(stem + suffix)
        if entry is not None:
            mask = entries.get(stem + "_[mask].png")
            return (_rgba_png(game_archive.read_file(archive, entry),
                              game_archive.read_file(archive, mask) if mask else None),
                    "FM24 bundled " + entry.path)
    return None


def find_existing_portrait(person_id: str, graphics_dir: Path, game_dir: Path | None) -> tuple[bytes, str] | None:
    """The person's current portrait as an RGBA PNG and where it came from.

    ``graphics_dir`` is FM Face's own folder inside FM24's graphics folder.
    Returns None when FM24 would show no picture (it draws a silhouette).
    """
    person_id = validate_id(person_id)
    found = _user_portrait(graphics_dir.parent, graphics_dir, _targets(person_id))
    if found is None and game_dir is not None:
        found = _bundled_portrait(game_dir, person_id)
    return found


# For "has this person a face?" while FM24's portrait menu opens: parsed
# configs are kept until the file changes, and the folder walk for configs
# outside FM Face's own folder is repeated at most every CONFIG_RESCAN seconds.
CONFIG_RESCAN = 30.0
_index_lock = threading.Lock()
_config_folders: dict[Path, tuple[float, list[Path]]] = {}
_config_people: dict[Path, tuple[tuple[int, int], dict[str, list[str]]]] = {}


def _people(config: Path) -> dict[str, list[str]]:
    """Person ID -> mapped file stems for one config.xml (cached by mtime and size)."""
    stat = config.stat()
    key = (stat.st_mtime_ns, stat.st_size)
    cached = _config_people.get(config)
    if cached and cached[0] == key:
        return cached[1]
    people: dict[str, list[str]] = {}
    if stat.st_size <= MAX_CONFIG:
        try:
            root = ET.fromstring(config.read_bytes())
        except ET.ParseError:
            root = None
        prefix = "graphics/pictures/person/"
        for record in root.iter("record") if root is not None else ():
            target = (record.get("to") or "").strip().replace("\\", "/").casefold()
            stem = record.get("from")
            if stem and target.startswith(prefix) and target.endswith("/portrait"):
                people.setdefault(target[len(prefix):-len("/portrait")].removeprefix("r-"), []).append(stem)
    _config_people[config] = (key, people)
    return people


def _configs(graphics_root: Path, preferred: Path, now: float) -> list[Path]:
    cached = _config_folders.get(graphics_root)
    if cached is None or now - cached[0] > CONFIG_RESCAN:
        found = []
        for folder, directories, files in os.walk(graphics_root):
            directories.sort()
            if "config.xml" in files and Path(folder) != preferred:
                found.append(Path(folder) / "config.xml")
        cached = _config_folders[graphics_root] = (now, found)
    return [preferred / "config.xml", *cached[1]]


def _bundled_ready(archive: Path) -> bool:
    """Whether the archive's index is built; if not, build it in the background."""
    try:
        key = (str(archive), archive.stat().st_mtime_ns)
    except OSError:
        return True  # No archive: nothing to wait for.
    with _bundled_lock:
        if key in _bundled:
            return True
    threading.Thread(target=lambda: _bundled_index(archive), daemon=True).start()
    return False


def has_existing_portrait(person_id: str, graphics_dir: Path, game_dir: Path | None, *, clock=time.monotonic,
                          wait=True) -> bool:
    """Whether find_existing_portrait would find a portrait, without decoding it.

    With ``wait=False`` a game archive whose index isn't built yet (about half
    a second) raises RuntimeError at once and is indexed in the background."""
    person_id = validate_id(person_id)
    with _index_lock:
        for config in _configs(graphics_dir.parent, graphics_dir, clock()):
            try:
                stems = _people(config).get(person_id, ())
                for stem in stems:
                    for suffix in IMAGE_SUFFIXES:
                        path = config.parent / (stem + suffix)
                        if path.is_file() and path.stat().st_size <= MAX_IMAGE:
                            return True
            except OSError:
                continue
    if game_dir is None:
        return False
    archive = game_dir / "data" / "game" / "graphics.fmf"
    if not archive.is_file():
        return False
    if not wait and not _bundled_ready(archive):
        raise RuntimeError("FM24's bundled pictures are still being indexed.")
    entries, mapped = _bundled_index(archive)
    stem = mapped.get(person_id)
    return stem is not None and any(stem + suffix in entries for suffix in IMAGE_SUFFIXES)


def game_dir_from_process(pid: int) -> Path | None:
    """FM24's installation folder, from the running game's fm.exe."""
    executable = process_executable(pid)
    return executable.parent if executable else None
