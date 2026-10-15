#!/usr/bin/env python3
"""Build a disposable FM24 skin from the user's extracted vanilla overview panel."""

from __future__ import annotations

import argparse
import html
import xml.etree.ElementTree as ET
from pathlib import Path
from urllib.parse import urlsplit


def build(panel_source: Path, skin_dir: Path, callback_url: str | None, *,
          skin_name: str = "FM Face Probe", button_text: str | None = "FM Face probe") -> None:
    if callback_url is not None:
        parsed = urlsplit(callback_url)
        if parsed.scheme != "http" or parsed.hostname not in {"127.0.0.1", "localhost"}:
            raise ValueError("The probe URL must use HTTP on loopback.")
        if parsed.username or parsed.password or parsed.fragment:
            raise ValueError("The probe URL contains unsupported components.")

    original = panel_source.read_text(encoding="utf-8-sig")
    ET.fromstring(original)
    marker = "\t\t<!-- Middle row of large panels -->"
    if original.count(marker) != 1:
        raise ValueError("This does not look like FM24's vanilla player overview panel.")
    click_event = (f'\t\t\t<record id="click_event" event_id="OURL" url="{html.escape(callback_url, quote=True)}" />\n'
                   if callback_url else '')
    button = (
        '\t\t<container height="32">\n'
        '\t\t\t<attachment_group class="horizontal_arrange" horizontal_alignment="left" />\n'
        '\t\t\t<widget class="primary_button" id="fmfb" auto_size="horizontal" height="30" text="test">\n' +
        click_event +
        f'\t\t\t<translation id="text" type="new" value="{html.escape(button_text, quote=True)}" />\n'
        '\t\t\t</widget>\n'
        '\t\t</container>\n\n'
    ) if button_text is not None else '\t\t<container height="32" />\n\n'
    modified = original.replace(marker, button + marker, 1)
    ET.fromstring(modified)

    if skin_dir.exists() and any(skin_dir.iterdir()):
        raise FileExistsError(f"Refusing to overwrite an existing skin: {skin_dir}")
    panel_out = skin_dir / "panels" / "player" / "player overview panel.xml"
    panel_out.parent.mkdir(parents=True, exist_ok=True)
    panel_out.write_text(modified, encoding="utf-8")
    (skin_dir / "skin_config.xml").write_text(
        '<record>\n'
        f'  <translation id="name" type="new" value="{html.escape(skin_name, quote=True)}"/>\n'
        '  <string id="author" value="FM Face"/>\n'
        '  <translation id="description" type="new" value="FM24 on-demand portraits"/>\n'
        '  <string id="version" value="0.1"/>\n'
        '  <flags id="parent" value="fm"/>\n'
        f'  <string id="skin_name" value="{html.escape(skin_name, quote=True)}"/>\n'
        '  <string id="required_version" value="24.0.0"/>\n'
        '</record>\n',
        encoding="utf-8",
    )
    print(f"Built disposable FM24 skin at {skin_dir}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--panel-source", type=Path, required=True)
    parser.add_argument("--skin-dir", type=Path, required=True)
    parser.add_argument("--callback-url", default="http://127.0.0.1:8768/probe")
    args = parser.parse_args()
    build(args.panel_source, args.skin_dir, args.callback_url)


if __name__ == "__main__":
    main()
