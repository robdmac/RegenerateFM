"""Add original RegenerateFM popup widgets to the user's installed FM24 panels."""

from __future__ import annotations

import xml.etree.ElementTree as ET


def popup(widget_id: str) -> ET.Element:
    """A transparent popup over the portrait. FM24 posts its select_event, FMFG,
    for either row; the DLL reads the chosen row (FMFG or FMFU) from the event."""
    widget = ET.Element("widget", {"class": "popup_button", "id": widget_id, "select_event": "FMFG",
                                   "fixed": "true", "auto_size": "none", "width": "88", "height": "80",
                                   "show_button_appearance": "false"})
    ET.SubElement(widget, "layout", {"class": "stick_to_sides_attachment",
                                    "alignment": "left,vertical", "inset": "0"})
    ET.SubElement(widget, "translation", {"id": "hint", "type": "new",
                                         "value": "Choose a face action"})
    ET.SubElement(widget, "string", {"id": "text", "value": ""})
    items = ET.SubElement(widget, "list", {"id": "items"})
    for item_id, text in (("FMFG", "Generate new face"), ("FMFU", "Update face")):
        item = ET.SubElement(items, "record", {"id": item_id})
        ET.SubElement(item, "translation", {"id": "text", "type": "new", "value": text})
    return widget


PANEL_KINDS = ("player", "staff")


def modify_panel(source: str, kind: str = "player") -> str:
    if kind not in PANEL_KINDS:
        raise ValueError(f"Unknown RegenerateFM panel kind: {kind}")
    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
    root = ET.fromstring(source, parser=parser)
    if any(node.get("id") in ("fmnP", "fmnS") for node in root.iter()):
        raise ValueError("This panel already has a native RegenerateFM popup.")
    if kind == "staff":
        portraits = [node for node in root.iter("widget") if node.get("id") == "ppic"]
        if len(portraits) != 1:
            raise ValueError("Could not identify the vanilla staff portrait widget.")
        parent = next(node for node in root.iter() if portraits[0] in list(node))
        parent.append(popup("fmnS"))
    else:
        headers = [node for node in root.iter("widget")
                   if node.get("class") == "player_personal_details_panel" and node.get("id") == "ppdp"]
        if len(headers) != 1:
            raise ValueError("Could not identify the vanilla player portrait header.")
        header = headers[0]
        parent = next(node for node in root.iter() if header in list(node))
        position = list(parent).index(header)
        wrapper = ET.Element("container", {"height": header.get("height", "80")})
        ET.SubElement(header, "layout", {"class": "stick_to_sides_attachment",
                                        "alignment": "all", "inset": "0"})
        parent.remove(header)
        wrapper.append(header)
        wrapper.append(popup("fmnP"))
        parent.insert(position, wrapper)
    ET.indent(root, space="\t")
    return ET.tostring(root, encoding="unicode") + "\n"
