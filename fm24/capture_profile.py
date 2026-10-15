#!/usr/bin/env python3
"""Extract a validated FM24 player profile from the tested overview layout.

No network requests or portrait writes occur here. Unknown layouts and missing
fields fail closed so an OCR error cannot silently create a paid image request.
"""

from __future__ import annotations

import argparse
import difflib
import json
import re
from pathlib import Path

from PIL import Image, ImageOps
from rapidocr import RapidOCR

from read_profile import capture_window, read_identity

BASE_SIZE = (1854, 1011)
PHYSICAL = ("Acceleration", "Agility", "Balance", "Jumping Reach",
            "Natural Fitness", "Pace", "Stamina", "Strength")
TECHNICAL = ("Corners", "Crossing", "Dribbling", "Finishing", "First Touch",
             "Free Kick Taking", "Heading", "Long Shots", "Long Throws",
             "Marking", "Passing", "Penalty Taking", "Tackling", "Technique")
MENTAL = ("Aggression", "Anticipation", "Bravery", "Composure", "Concentration",
          "Decisions", "Determination", "Flair", "Leadership", "Off The Ball",
          "Positioning", "Teamwork", "Vision", "Work Rate")
GOALKEEPING = ("Aerial Reach", "Command Of Area", "Communication", "Eccentricity",
               "First Touch", "Handling", "Kicking", "One On Ones", "Passing",
               "Punching (Tendency)", "Reflexes", "Rushing Out (Tendency)", "Throwing")


def _rect(image: Image.Image, box: tuple[int, int, int, int]) -> Image.Image:
    sx = image.width / BASE_SIZE[0]
    sy = image.height / BASE_SIZE[1]
    return image.crop((round(box[0] * sx), round(box[1] * sy),
                       round(box[2] * sx), round(box[3] * sy)))


def _ocr(image: Image.Image, engine: RapidOCR, scale: int = 3):
    scaled = image.resize((image.width * scale, image.height * scale), Image.Resampling.LANCZOS)
    result = engine(scaled)
    items = []
    for text, box, score in zip(result.txts if result.txts is not None else (),
                                result.boxes if result.boxes is not None else (),
                                result.scores if result.scores is not None else ()):
        x = sum(point[0] for point in box) / (4 * scale)
        y = sum(point[1] for point in box) / (4 * scale)
        items.append((text.strip(), x, y, score))
    return items


def _header(image: Image.Image, engine: RapidOCR) -> tuple[int, str]:
    items = _ocr(_rect(image, (260, 112, 700, 200)), engine)
    ages = [int(match[1]) for text, _, _, _ in items
            if (match := re.search(r"\b(\d{2})\s+years?\s+old\b", text, re.I))]
    nationalities = [text for text, _, _, _ in items
                     if re.fullmatch(r"[A-Za-zÀ-ÿ][A-Za-zÀ-ÿ .'-]{2,35}", text)
                     and "caps" not in text.lower()]
    if len(ages) != 1 or len(nationalities) != 1 or not 14 <= ages[0] <= 60:
        raise ValueError("Could not verify the player's age and nationality in the FM24 overview.")
    return ages[0], nationalities[0]


def _stat_column(image: Image.Image, engine: RapidOCR, x1: int, x2: int,
                 label: str, expected: tuple[str, ...], *, exact: bool) -> tuple[dict[str, int], float | None]:
    items = _ocr(_rect(image, (x1, 205, x2, 605)), engine)
    headings = [(text, y) for text, _, y, _ in items if text.upper() == label.upper()]
    if len(headings) != 1:
        raise ValueError(f"Could not find the {label.lower()} attributes on the FM24 overview.")
    heading_y = headings[0][1]
    stats: dict[str, int] = {}
    height_y = None
    values = [(int(text), x, y) for text, x, y, _ in items
              if re.fullmatch(r"(?:[1-9]|1\d|20)", text) and x > (x2 - x1) * .65]
    for text, x, y, score in items:
        if y <= heading_y + 10 or x > (x2 - x1) * .65 or score < .75:
            continue
        if text.lower() == "height" and label == "PHYSICAL":
            height_y = y + 205
            continue
        match = difflib.get_close_matches(text.lower(),
                                          (name.lower() for name in expected if name[0].lower() == text[:1].lower()),
                                          n=1, cutoff=.82)
        if not match:
            continue
        name = next(name for name in expected if name.lower() == match[0])
        near = [value for value, vx, vy in values if abs(vy - y) < 9]
        if len(near) != 1:
            # The whole-column pass can miss a digit (notably repeated 9s).
            # The exact physical pass below rereads each missing row tightly.
            continue
        if name in stats:
            raise ValueError(f"Duplicate FM24 attribute {name}.")
        stats[name] = near[0]
    if exact and set(stats) != set(expected):
        for index, name in enumerate(expected):
            if name in stats:
                continue
            center = 205 + heading_y + 23 * (index + 1)
            row = _ocr(_rect(image, (x1, round(center - 11), x2, round(center + 11))), engine, scale=4)
            labels = [text for text, x, _, _ in row if x < (x2 - x1) * .65]
            scores = [int(text) for text, x, _, _ in row
                      if re.fullmatch(r"(?:[1-9]|1\d|20)", text) and x > (x2 - x1) * .65]
            if len(labels) == 1 and labels[0].lower() == name.lower() and len(scores) == 1:
                stats[name] = scores[0]
        if set(stats) != set(expected):
            raise ValueError("Could not read all eight physical attributes from FM24.")
    if not exact and len(stats) < 6:
        raise ValueError(f"Could not read enough {label.lower()} attributes from FM24.")
    return stats, height_y


def _height(image: Image.Image, engine: RapidOCR, y: float) -> str:
    # A tight grayscale crop prevents the feet/inches punctuation from merging
    # into adjacent text. Whole-column OCR misread 6'1" as 64" in testing.
    line = ImageOps.grayscale(_rect(image, (1240, round(y - 12), 1305, round(y + 13))))
    items = _ocr(line, engine, scale=5)
    candidates = [text for text, _, _, _ in items]
    if len(candidates) != 1:
        raise ValueError("Could not read the player's height in FM24.")
    height = candidates[0]
    if re.fullmatch(r"[4-7]'(?:[0-9]|1[01])\"", height):
        return height
    if re.fullmatch(r"(?:1\d\d|20\d|21\d)\s*cm", height, re.I):
        return height
    raise ValueError("Could not verify the player's height in FM24.")


def _biography(image: Image.Image, engine: RapidOCR) -> str:
    items = _ocr(_rect(image, (1460, 205, 1835, 605)), engine, scale=2)
    items.sort(key=lambda item: item[2])
    headings = [i for i, (text, _, _, _) in enumerate(items) if text.upper().startswith("BIOGRAPHY")]
    if len(headings) != 1:
        return ""
    start = headings[0] + 1
    lines = []
    last_y = items[headings[0]][2]
    for text, _, y, score in items[start:]:
        if y - last_y > 25:
            break
        if score < .70:
            break
        lines.append(text)
        last_y = y
    return " ".join(lines)[:600] if lines else ""


def read_player_profile(image: Image.Image, engine: RapidOCR | None = None) -> tuple[str, dict]:
    if abs(image.width / image.height - BASE_SIZE[0] / BASE_SIZE[1]) > .03:
        raise ValueError("FM24 window aspect ratio differs from the tested overview layout.")
    engine = engine or RapidOCR()
    identity = read_identity(image, engine)
    age, nationality = _header(image, engine)
    first_items = _ocr(_rect(image, (718, 205, 914, 605)), engine)
    headings = {text.upper() for text, _, _, _ in first_items}
    if "TECHNICAL" in headings:
        section, expected = "technical", TECHNICAL
    elif "GOALKEEPING" in headings:
        section, expected = "goalkeeping", GOALKEEPING
    else:
        raise ValueError("Could not identify the FM24 attribute layout.")
    first, _ = _stat_column(image, engine, 718, 914, section.upper(), expected, exact=False)
    mental, _ = _stat_column(image, engine, 914, 1105, "MENTAL", MENTAL, exact=False)
    physical, height_y = _stat_column(image, engine, 1105, 1305, "PHYSICAL", PHYSICAL, exact=True)
    if height_y is None and section == "technical":
        # For outfield players the height line follows the eight physical rows.
        headings = _ocr(_rect(image, (1105, 205, 1305, 250)), engine)
        physical_header = [y for text, _, y, _ in headings if text.upper() == "PHYSICAL"]
        if len(physical_header) == 1:
            height_y = 205 + physical_header[0] + 23 * 9
    if height_y is None:
        raise ValueError("Could not locate the player's height in FM24.")
    profile = {
        "type": "player", "name": identity.name, "age": age,
        "nationality": nationality, "position": identity.role,
        "club": identity.club, "height": _height(image, engine, height_y),
        "physical": physical, "mental": mental, section: first,
    }
    biography = _biography(image, engine)
    if biography:
        profile["biographyHeadline"] = biography
    return identity.player_id, profile


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--image", type=Path)
    source.add_argument("--window-id")
    args = parser.parse_args()
    screenshot = Image.open(args.image) if args.image else capture_window(args.window_id)
    player_id, profile = read_player_profile(screenshot)
    print(json.dumps({"playerId": player_id, "profile": profile}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
