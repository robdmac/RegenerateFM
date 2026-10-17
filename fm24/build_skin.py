#!/usr/bin/env python3
"""Build FM24 player and staff panels for the portrait click listener."""

from __future__ import annotations

import argparse
import os
import secrets
import xml.etree.ElementTree as ET
from pathlib import Path

SKIN_CONFIG = """<record>
  <translation id="name" type="new" value="FM Face"/>
  <string id="author" value="FM Face"/>
  <translation id="description" type="new" value="FM24 on-demand portraits"/>
  <string id="version" value="0.1"/>
  <flags id="parent" value="fm"/>
  <string id="skin_name" value="FM Face"/>
  <string id="required_version" value="24.0.0"/>
</record>
"""


def add_player_panel(panel_source: Path, skin_dir: Path) -> Path:
    """Copy the vanilla player Overview with room below the portrait header."""
    original = panel_source.read_text(encoding="utf-8-sig")
    ET.fromstring(original)
    marker = "\t\t<!-- Middle row of large panels -->"
    if original.count(marker) != 1:
        raise ValueError("This does not look like FM24's vanilla player overview panel.")
    modified = original.replace(marker, '\t\t<container height="32" />\n\n' + marker, 1)
    ET.fromstring(modified)
    if skin_dir.exists() and any(skin_dir.iterdir()):
        raise FileExistsError(f"Refusing to overwrite an existing skin: {skin_dir}")
    destination = skin_dir / "panels" / "player" / "player overview panel.xml"
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(modified, encoding="utf-8")
    destination.chmod(0o600)
    (skin_dir / "skin_config.xml").write_text(SKIN_CONFIG, encoding="utf-8")
    return destination


def add_staff_panel(panel_source: Path, skin_dir: Path) -> Path:
    original = panel_source.read_text(encoding="utf-8-sig")
    root = ET.fromstring(original)
    layouts = [child for child in root if child.tag == "layout" and child.get("class") == "arrange_vertical_attachment"]
    if len(layouts) != 1 or layouts[0].get("layout") != "95, -1":
        raise ValueError("This does not look like FM24's vanilla non-player profile panel.")
    if sum(child.tag == "container" for child in root) != 2:
        raise ValueError("Unexpected FM24 staff panel structure.")
    marker = "\t</container>\n\n\n\t<container>"
    if original.count(marker) != 1:
        raise ValueError("Could not locate the FM24 staff header boundary.")
    spacer = '\t<container height="32" />\n\n'
    modified = original.replace('layout="95, -1"', 'layout="95, 32, -1"', 1)
    modified = modified.replace(marker, "\t</container>\n\n" + spacer + "\t<container>", 1)
    ET.fromstring(modified)
    destination = skin_dir / "panels" / "non_player" / "non player profile.xml"
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(modified, encoding="utf-8")
    destination.chmod(0o600)
    return destination


def ensure_token(auth_file: Path) -> str:
    """The private token the DLL and companion share, created on first use."""
    if auth_file.exists():
        token = auth_file.read_text(encoding="ascii").strip()
        if len(token) < 40:
            raise ValueError("Existing FM24 callback token is invalid.")
        return token
    token = secrets.token_urlsafe(32)
    auth_file.parent.mkdir(parents=True, exist_ok=True)
    with open(auth_file, "x", encoding="ascii", opener=lambda path, flags: os.open(path, flags, 0o600)) as handle:
        handle.write(token + "\n")
    return token


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--panel-source", required=True, type=Path)
    parser.add_argument("--staff-panel-source", type=Path,
                        help="Extracted panels/non_player/non player profile.xml for staff portraits")
    parser.add_argument("--skin-dir", required=True, type=Path)
    parser.add_argument("--auth-file", required=True, type=Path)
    args = parser.parse_args()
    ensure_token(args.auth_file)
    add_player_panel(args.panel_source, args.skin_dir)
    if args.staff_panel_source:
        add_staff_panel(args.staff_panel_source, args.skin_dir)
    print(f"FM24 skin and private callback token are ready. Keep {args.auth_file} private.")


if __name__ == "__main__":
    main()
