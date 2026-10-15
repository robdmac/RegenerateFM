"""Versioned FM24 portrait files to avoid its same-filename graphics cache."""

from __future__ import annotations

import re
import secrets
import xml.etree.ElementTree as ET
from pathlib import Path

from fmface import PNG_SIGNATURE, _atomic_write, graphics_maps_xml, load_graphics_maps, validate_id


def save_versioned_portrait(directory: Path, player_id: str, png: bytes) -> tuple[Path, list[Path]]:
    player_id = validate_id(player_id)
    if not png.startswith(PNG_SIGNATURE):
        raise ValueError("Portrait must be a PNG image.")
    config = directory / "config.xml"
    tree, maps = load_graphics_maps(directory)
    destinations = (
        f"graphics/pictures/person/{player_id}/portrait",
        f"graphics/pictures/person/r-{player_id}/portrait",
    )
    previous = []
    for record in list(maps):
        if record.tag == "record" and record.get("to") in destinations:
            stem = record.get("from", "")
            if re.fullmatch(rf"{re.escape(player_id)}-[0-9a-f]{{16}}", stem):
                old = directory / f"{stem}.png"
                if old not in previous:
                    previous.append(old)
            maps.remove(record)
    stem = f"{player_id}-{secrets.token_hex(8)}"
    for destination in destinations:
        ET.SubElement(maps, "record", {"from": stem, "to": destination})
    xml = graphics_maps_xml(tree)
    portrait = directory / f"{stem}.png"
    _atomic_write(portrait, png)
    try:
        _atomic_write(config, xml)
    except Exception:
        portrait.unlink(missing_ok=True)
        raise
    return portrait, previous
