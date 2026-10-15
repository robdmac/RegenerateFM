"""Read a validated FM24 staff profile from the tested non-player layout."""

from __future__ import annotations

import re

from PIL import Image
from rapidocr import RapidOCR

from capture_profile import BASE_SIZE, _biography, _ocr, _rect

STAFF_LINE = re.compile(r"(?P<job>.+?)\s+-\s+(?P<club>.+?)\s+\(ID:\s*(?:r-)?(?P<id>\d{1,20})\)", re.I)


def read_staff_identity(image: Image.Image, engine: RapidOCR) -> tuple[str, str, str, str]:
    title = _ocr(_rect(image, (320, 0, 1200, 75)), engine)
    matches = [match for text, _, _, _ in title if (match := STAFF_LINE.fullmatch(text))]
    names = [text for text, x, y, _ in title
             if x > 70 and y < 35 and len(text) > 2 and not STAFF_LINE.fullmatch(text)]
    if len(matches) != 1 or len(names) != 1:
        raise ValueError("Could not verify the staff name, job, and ID. Enable screen IDs in FM24 Preferences.")
    identity = matches[0]
    return identity["id"], names[0], identity["job"].strip(), identity["club"].strip()


def read_staff_profile(image: Image.Image, engine: RapidOCR | None = None) -> tuple[str, dict]:
    if image.size != BASE_SIZE:
        raise ValueError("FM24 staff capture requires the tested 1854×1011 window.")
    engine = engine or RapidOCR()
    roles = _ocr(_rect(image, (180, 220, 500, 310)), engine)
    if not any(text.upper() == "STAFF ROLES" for text, _, _, _ in roles):
        raise ValueError("The open FM24 screen is not a staff Overview.")
    staff_id, name, job, club = read_staff_identity(image, engine)

    details = _ocr(_rect(image, (260, 115, 790, 200)), engine)
    ages = [int(match[1]) for text, _, _, _ in details
            if (match := re.search(r"\b(\d{2})\s+years?\s+old\b", text, re.I))]
    nationalities = [text for text, x, y, _ in details
                     if re.fullmatch(r"[A-Za-zÀ-ÿ][A-Za-zÀ-ÿ .'-]{2,35}", text)
                     and x < 200 and 25 <= y <= 48]
    if len(ages) != 1 or not 18 <= ages[0] <= 100 or len(nationalities) != 1:
        raise ValueError("Could not verify staff age and nationality on the FM24 Overview.")

    profile = {"type": "staff", "name": name, "age": ages[0],
               "nationality": nationalities[0], "jobTitle": job}
    if club.lower() != "unemployed":
        profile["club"] = club
    biography = _biography(image, engine)
    if biography and not biography.lower().startswith("no biography is available"):
        profile["biographyHeadline"] = biography
    return staff_id, profile
