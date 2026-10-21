"""Stable, understated appearance choices for generated FM portraits."""

from __future__ import annotations

import hashlib


TRAIT_KEYS = ("face", "features", "hair", "facialHair", "expression")
GENDERS = ("female", "male")
# Facial hair is only ever chosen for a man: anyone else, including a person
# whose gender isn't known, gets this.
NO_FACIAL_HAIR = "no facial hair"


def appearance_traits(seed: str, age: int, gender: str | None = None) -> dict[str, str]:
    """Choose independently per trait; never use Python's randomized hash().

    ``gender`` is "female", "male" or None (not known). Women get their own
    hairstyles, and only men get facial hair."""
    def pick(category, choices):
        digest = hashlib.sha256(f"regeneratefm-appearance-v1\0{seed}\0{category}".encode()).digest()
        return choices[int.from_bytes(digest[:8], "big") % len(choices)]

    shape = pick("faceShape", ("a relatively long face", "a broad face", "an oval face",
                              "a square face", "a narrow face", "a rounder face"))
    jaw = pick("jaw", ("a softly angular jaw", "a gently rounded jaw", "a modest chin",
                       "a broad chin", "a slightly tapered jaw"))
    cheeks = pick("cheeks", ("fuller cheeks", "lean cheeks", "subtly uneven cheekbones",
                             "slightly hollow cheeks"))
    face = f"{shape}, with {jaw} and {cheeks}"
    features = pick("features", (
        "a broad, rounded nose and straight, moderately thick eyebrows",
        "a narrow nose with a softly rounded tip and low, straight eyebrows",
        "a slightly prominent nose bridge and fine, gently arched eyebrows",
        "a short, broad nose and thick eyebrows with a small natural unevenness",
        "slightly prominent ears and an ordinary straight nose",
        "a rounded nose tip and eyebrows that sit at subtly different heights",
        "a long, straight nose and widely spaced, moderately thick eyebrows",
        "a gently convex nose bridge and short, straight eyebrows",
    ))
    if gender == "female":
        styles = (
            "hair tied back in a neat ponytail", "hair pulled back into a low bun", "a high ponytail",
            "shoulder-length straight hair tucked behind the ears", "a short pixie cut",
            "tightly coiled hair pulled back from the face", "braids tied back", "a chin-length bob",
            "curly hair tied up loosely", "short, cropped natural hair",
        )
        style = pick("hairStyleFemale", styles)
    else:
        styles = (
            "a close buzz cut", "short, tightly curled hair", "short, straight hair brushed forward",
            "short, coiled hair with a simple rounded outline", "a plain short side-parted haircut",
            "medium-length, loosely wavy hair pushed back", "a shaved head",
            "short hair with an uneven fringe", "longer curls loosely swept away from the forehead",
            "short, close-cropped hair with a naturally uneven hairline",
        )
        if age >= 25:
            styles += ("very short hair with a mildly receding hairline",)
        style = pick("hairStyle", styles)
    colours = ("black", "dark brown", "medium brown", "light brown", "dark blond", "auburn")
    if age >= 45:
        colours += ("salt-and-pepper", "greying brown", "silver-grey")
    hair = style if style == "a shaved head" else f"{style}; hair colour: {pick('hairColour', colours)}"
    facial_hair = ("clean-shaven",)
    if age >= 18:
        facial_hair += ("clean-shaven", "very light, patchy stubble", "a faint moustache")
    if age >= 23:
        facial_hair += ("short, uneven stubble", "a short, neatly trimmed beard", "a modest moustache")
    facial_hair = pick("facialHair", facial_hair) if gender == "male" else NO_FACIAL_HAIR
    expression = pick("expression", (
        "a relaxed neutral expression with closed lips",
        "a relaxed neutral expression with closed lips",
        "a calm, unsmiling expression with relaxed facial muscles",
        "a relaxed expression with a barely perceptible, slightly uneven smile",
        "a small, natural closed-mouth smile",
        "a restrained, natural smile showing a little teeth",
    ))
    return dict(zip(TRAIT_KEYS, (face, features, hair, facial_hair, expression)))


def appearance_instruction(traits: dict[str, str]) -> str:
    if (not isinstance(traits, dict) or set(traits) != set(TRAIT_KEYS)
            or any(not isinstance(value, str) or not value.strip() or len(value) > 200
                   for value in traits.values())):
        raise ValueError("Saved appearance traits are invalid.")
    return (
        "Individual appearance: " + "; ".join(traits[key] for key in TRAIT_KEYS) + ". "
        "Keep these features understated, anatomically natural and consistent with the person's age. "
        "Preserve subtle facial asymmetry and ordinary skin texture. "
    )
