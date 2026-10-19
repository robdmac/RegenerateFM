#!/usr/bin/env python3
"""Local, on-demand Football Manager portrait generator."""

from __future__ import annotations

import argparse
import base64
import binascii
import html
import hmac
import io
import json
import os
import re
import secrets
import sys
import tempfile
import threading
import urllib.error
import urllib.parse
import urllib.request
import webbrowser
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import keystore
from appearance import appearance_instruction, appearance_traits

# A packaged FM24 companion (fm24/build_exe.py) keeps .env beside its executable.
ROOT = Path(sys.executable if getattr(sys, "frozen", False) else __file__).resolve().parent
DEFAULT_OUTPUT = ROOT / "output" / "fm26_faces"
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
ID_RE = re.compile(r"^[0-9]{1,20}$")
MAX_BODY = 32_768
# Football Manager draws portraits at 256×256; the image API's smallest
# square output is 1024×1024, so generated faces are scaled down to this.
PORTRAIT_SIZE = 256
# The largest existing portrait sent to the image edit endpoint.
MAX_REFERENCE = 8 << 20
PHYSICAL_STATS = (
    "Acceleration", "Agility", "Balance", "Jumping Reach",
    "Natural Fitness", "Pace", "Stamina", "Strength",
)
PORTRAIT_FRAMING = (
    "Match the tight headshot framing of existing Football Manager squad photos. "
    "The head should fill the square image: crown-to-chin height about 80–85% of the image, "
    "with the full hairstyle and chin visible and only a small gap above the hair. "
    "Crop at the base of the neck, showing just the collar and a narrow sliver of the shirt; "
    "do not include the upper chest or broad shoulders. "
    "Keep the face centered, looking at the camera, with a natural, slight variation in head angle. "
)
PORTRAIT_PHOTOGRAPHY = (
    "Use everyday club media-day photography with restrained photographic processing, "
    "gently directional lighting and natural facial "
    "shadows and colour variation. Preserve skin texture, pores, small imperfections and "
    "age-appropriate lines, with natural facial asymmetry and no beauty retouching. "
)
PORTRAIT_BACKGROUND = (
    "Use a genuinely transparent background with clean hair edges, "
    "without a backdrop, halo or vignette. "
)
PORTRAIT_STYLE = (
    PORTRAIT_FRAMING +
    "Follow the individual's supplied expression; otherwise use a relaxed neutral expression. "
    + PORTRAIT_PHOTOGRAPHY +
    "Give this individual distinctive facial proportions, nose, jaw, eyes, hairline and hairstyle "
    "consistent with the supplied appearance details and age. Allow varied hair lengths and "
    "textures and age-appropriate facial hair; avoid defaulting to a model-like face or the "
    "same tousled brown haircut. " + PORTRAIT_BACKGROUND
)


def load_env(path: Path = ROOT / ".env", use_key_file: bool = True) -> dict[str, str]:
    values = dict(os.environ)
    if path.exists():
        for line in path.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            key = key.strip()
            if not values.get(key):
                values[key] = value.strip().strip('"').strip("'")
    # On Windows the FM24 companion saves the key it asks for in FM Face's key
    # file, which the FM26 plugin shares.
    store = keystore.default_path()
    if use_key_file and store and not values.get("OPENAI_API_KEY", "").strip():
        try:
            values["OPENAI_API_KEY"] = keystore.load(store) or ""
        except (OSError, ValueError) as exc:
            print(f"Could not read the saved OpenAI key: {exc}", file=sys.stderr, flush=True)
    return values


def validate_id(value: str) -> str:
    if not ID_RE.fullmatch(value):
        raise ValueError("Player ID must contain only digits (up to 20).")
    return value


def output_dir(env: dict[str, str]) -> Path:
    configured = env.get("FM_GRAPHICS_DIR", "").strip()
    return Path(configured).expanduser() if configured else DEFAULT_OUTPUT


def portrait_prompt(description: str) -> str:
    description = description.strip()
    if not description:
        raise ValueError("Describe the player's appearance before generating.")
    if len(description) > 800:
        raise ValueError("Description must be 800 characters or fewer.")
    return (
        "Create one fictional footballer's tight profile headshot for a sports management game. "
        "Adult subject. " + PORTRAIT_STYLE +
        "Wear a football shirt in the kit and colours of the team specified in the description. "
        "If no team is specified, use a plain unbranded football shirt. "
        "No text, logos, badges, borders, or other people. "
        "Appearance: " + description
    )


def _profile_text(profile: dict, key: str, kind: str = "Player") -> str:
    value = profile.get(key)
    if not isinstance(value, str) or not value.strip() or len(value) > 120:
        raise ValueError(f"{kind} {key} is missing or invalid.")
    return " ".join(value.split())


def _biography_headline(profile: dict) -> str:
    value = profile.get("biographyHeadline")
    if value is None or value == "":
        return ""
    if not isinstance(value, str) or len(value) > 1000:
        raise ValueError("Biography headline is invalid or too long.")
    value = re.split(r"\r?\n\s*\r?\n", value.strip(), maxsplit=1)[0]
    value = " ".join(value.split())
    return value[:600].rsplit(" ", 1)[0] if len(value) > 600 else value


def _profile_stats(value: object, section: str) -> dict[str, int | str]:
    if not isinstance(value, dict):
        raise ValueError(f"Player {section} stats are missing.")
    result = {}
    for name, score in value.items():
        if not isinstance(name, str) or not name.strip() or len(name) > 60:
            raise ValueError(f"Player {section} has an invalid stat name.")
        if isinstance(score, bool):
            raise ValueError(f"Player {section} stat {name} must be 1–20 or a scouted range.")
        if isinstance(score, int) and 1 <= score <= 20:
            result[name.strip()] = score
        elif isinstance(score, str) and re.fullmatch(r"(?:[1-9]|1[0-9]|20)\s*[-–]\s*(?:[1-9]|1[0-9]|20)", score.strip()):
            low, high = map(int, re.split(r"\s*[-–]\s*", score.strip()))
            if low > high:
                raise ValueError(f"Player {section} stat {name} has an inverted range.")
            result[name.strip()] = f"{low}–{high}"
        else:
            raise ValueError(f"Player {section} stat {name} must be 1–20 or a scouted range.")
    return result


def _stat_strength(score: int | str) -> float:
    if isinstance(score, int):
        return score
    low, high = map(int, score.split("–"))
    return (low + high) / 2


def _appearance_prompt(name: str, age: int, person_id: str | None,
                       directory: Path | None) -> str:
    if person_id is None:
        if directory is not None:
            raise ValueError("A person ID is required to save an appearance profile.")
        return appearance_instruction(appearance_traits("name:" + name.casefold(), age))
    person_id = validate_id(person_id)
    path = Path(directory) / ".fmface-appearances" / f"{person_id}.json" if directory is not None else None
    if path is not None and path.exists():
        saved = json.loads(path.read_text(encoding="utf-8"))
        if (not isinstance(saved, dict) or saved.get("schemaVersion") != 1
                or saved.get("personId") != person_id):
            raise ValueError(f"Saved appearance profile is invalid: {path.name}")
        return appearance_instruction(saved.get("traits"))
    traits = appearance_traits(person_id, age)
    instruction = appearance_instruction(traits)
    if path is not None:
        saved = {"schemaVersion": 1, "personId": person_id, "traits": traits}
        _atomic_write(path, (json.dumps(saved, ensure_ascii=False, indent=2) + "\n").encode())
    return instruction


def _no_club(club: str) -> bool:
    return club.casefold() in ("free agent", "unattached", "no club")


def _kit_text(club: str) -> str:
    if _no_club(club):
        return "The player has no club; use a plain unbranded football shirt. "
    return (
        f"Make sure the player is wearing the home kit and colours of {club}, "
        "with the club's recognisable shirt colours and kit pattern. "
        "For a goalkeeper, use the club's goalkeeper kit. "
    )


def _profile_kind_and_age(profile: dict) -> tuple[str, str, int]:
    if not isinstance(profile, dict):
        raise ValueError("Profile is missing.")
    kind = profile.get("type", "player")
    if kind not in ("player", "staff"):
        raise ValueError("Profile type must be player or staff.")
    label = "Staff" if kind == "staff" else "Player"
    age = profile.get("age")
    if (isinstance(age, bool) or not isinstance(age, int) or
            not 14 <= age <= (100 if kind == "staff" else 60)):
        raise ValueError(f"{label} age is missing or invalid.")
    return kind, label, age


def update_prompt(profile: dict) -> str:
    """Instructions for refreshing an existing portrait supplied as the input image.

    Only the person's current age and attire change; everything else should
    stay as in the supplied picture.
    """
    kind, label, age = _profile_kind_and_age(profile)
    name = _profile_text(profile, "name", label)
    if kind == "staff":
        attire = "Dress them in a simple unbranded dark jacket or training top. "
    else:
        attire = _kit_text(_profile_text(profile, "club"))
    return (
        f"Update this Football Manager profile picture of {name}. The supplied image is "
        f"{name}'s current portrait. Keep them clearly the same person, with the same face shape, "
        "features, skin tone, eye colour, hairstyle, facial hair and distinguishing marks. "
        f"Show {name} as they look now, at age {age}: if the supplied picture looks younger or "
        "older, change only the natural signs of age, such as hairline, hair colour, skin "
        "texture and facial lines. " + attire + PORTRAIT_FRAMING + PORTRAIT_PHOTOGRAPHY +
        PORTRAIT_BACKGROUND + f"Otherwise make it an updated version of the supplied picture of {name}. "
        "Do not depict numbers. No text, logos, badges, borders, or other people."
    )


def profile_prompt(profile: dict, *, person_id: str | None = None,
                   appearance_dir: Path | None = None) -> str:
    if not isinstance(profile, dict):
        raise ValueError("Profile is missing.")
    kind = profile.get("type", "player")
    if kind not in ("player", "staff"):
        raise ValueError("Profile type must be player or staff.")
    label = "Staff" if kind == "staff" else "Player"
    name = _profile_text(profile, "name", label)
    nationality = _profile_text(profile, "nationality", label)
    biography = _biography_headline(profile)
    biography_text = f" Biography headline: {biography}{'' if biography.endswith(('.', '!', '?')) else '.'}" if biography else ""
    age = profile.get("age")
    if isinstance(age, bool) or not isinstance(age, int) or not 14 <= age <= 100:
        raise ValueError(f"{label} age is missing or invalid.")
    if kind == "staff":
        job_title = _profile_text(profile, "jobTitle", label)
        club = profile.get("club")
        if club is not None and club != "":
            club = _profile_text(profile, "club", label)
        club_text = f" at {club}" if club else ""
        appearance = _appearance_prompt(name, age, person_id, appearance_dir)
        return (
            f"Football Manager profile picture for {name}, age {age}, nationality {nationality}, "
            f"job title {job_title}{club_text}.{biography_text} "
            "Create one plausible, original staff portrait for a sports management game. "
            + appearance + PORTRAIT_STYLE +
            "Wear a simple unbranded dark jacket or training top. "
            "Use age and biography to guide appearance, but do not infer "
            "ethnicity from nationality. No text, logos, badges, borders, or other people."
        )
    position = _profile_text(profile, "position")
    club = _profile_text(profile, "club")
    height = _profile_text(profile, "height")
    if age > 60:
        raise ValueError("Player age is missing or invalid.")
    physical = _profile_stats(profile.get("physical"), "physical")
    missing = set(PHYSICAL_STATS) - physical.keys()
    if missing:
        raise ValueError("Missing physical stats: " + ", ".join(sorted(missing)))
    other = []
    for section in ("technical", "mental", "goalkeeping"):
        for stat, score in _profile_stats(profile.get(section, {}), section).items():
            other.append((section, stat, score))
    if len(other) < 6:
        raise ValueError("At least six technical, mental, or goalkeeping stats are required.")
    best = sorted(other, key=lambda item: (-_stat_strength(item[2]), item[1]))[:3]
    worst = sorted((item for item in other if item not in best), key=lambda item: (_stat_strength(item[2]), item[1]))[:3]
    fmt = lambda items: ", ".join(f"{section} {stat} {score}/20" for section, stat, score in items)
    first_name = name.split()[0]
    kit_text = _kit_text(club)
    plays = "who is a free agent" if _no_club(club) else f"who plays for {club}"
    appearance = _appearance_prompt(name, age, person_id, appearance_dir)
    return (
        f"Football Manager profile picture for {name}, a {age}-year-old {position} "
        f"{plays}. Nationality: {nationality}. {first_name} is {height} tall.{biography_text} "
        "Physical stats: " + ", ".join(f"{stat} {physical[stat]}/20" for stat in PHYSICAL_STATS) + ". "
        f"Best non-physical stats: {fmt(best)}. Weakest non-physical stats: {fmt(worst)}. "
        "Create one plausible, original footballer portrait for a sports management game. "
        + appearance + PORTRAIT_STYLE +
        f"{kit_text}Use age and physical details to guide appearance, but do not depict "
        "numbers or infer ethnicity from nationality. No text, logos, badges, borders, or other people."
    )


def api_base(env: dict[str, str]) -> str:
    """OPENAI_BASE_URL, for testing against a local stand-in (like the FM26
    plugin's ApiBaseUrl); OpenAI's own address by default. Plain http is
    accepted only for this computer."""
    return keystore.api_base(env.get("OPENAI_BASE_URL", ""))


def _image_settings(env: dict[str, str]) -> tuple[str, dict[str, str]]:
    key = env.get("OPENAI_API_KEY", "").strip()
    if not key:
        raise ValueError("OPENAI_API_KEY is missing. Add it to .env first.")
    quality = env.get("OPENAI_IMAGE_QUALITY", "medium").strip().lower() or "medium"
    if quality not in ("low", "medium", "high", "xhigh", "max", "auto"):
        raise ValueError("OPENAI_IMAGE_QUALITY must be low, medium, high, xhigh, max, or auto.")
    return key, {
        "model": env.get("OPENAI_IMAGE_MODEL", "gpt-image-2.5-sunburst"),
        "size": "1024x1024",
        "quality": quality,
        "background": "transparent",
        "output_format": "png",
    }


def _image_request(key: str, endpoint: str, body: bytes, content_type: str,
                   settings: dict[str, str], metadata: dict | None, base: str) -> bytes:
    request = urllib.request.Request(
        f"{base}/images/{endpoint}", data=body,
        headers={"Authorization": f"Bearer {key}", "Content-Type": content_type},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=180) as response:
            result = json.load(response)
    except urllib.error.HTTPError as exc:
        try:
            detail = json.loads(exc.read(4096)).get("error", {}).get("message", "")
        except (ValueError, AttributeError):
            detail = ""
        detail = str(detail or exc.reason).replace(key, "[REDACTED]")
        raise RuntimeError(f"OpenAI image API returned HTTP {exc.code}: {detail}") from exc
    except (urllib.error.URLError, TimeoutError) as exc:
        raise RuntimeError(f"Could not reach the OpenAI image API: {exc.reason if isinstance(exc, urllib.error.URLError) else exc}") from exc
    image = result.get("data", [{}])[0].get("b64_json")
    if not image:
        raise RuntimeError("Image API response did not contain an image.")
    try:
        png = base64.b64decode(image, validate=True)
    except (ValueError, binascii.Error) as exc:
        raise RuntimeError("Image API returned invalid image data.") from exc
    if not png.startswith(PNG_SIGNATURE):
        raise RuntimeError("Image API returned data that is not a PNG.")
    png = fit_portrait(png)
    if metadata is not None:
        metadata.update(model=settings["model"], quality=settings["quality"], size=settings["size"])
        if isinstance(result.get("usage"), dict):
            metadata["usage"] = result["usage"]
    return png


def fit_portrait(png: bytes) -> bytes:
    """Scale a generated portrait to FM's 256×256, keeping its transparency."""
    from PIL import Image  # Pillow: python3-pil, or pip install Pillow

    try:
        with Image.open(io.BytesIO(png)) as image:
            image.load()
            if image.size == (PORTRAIT_SIZE, PORTRAIT_SIZE):
                return png
            image = image.convert("RGBA").resize((PORTRAIT_SIZE, PORTRAIT_SIZE), Image.LANCZOS)
    except OSError as exc:
        raise RuntimeError("Image API returned a PNG that could not be read.") from exc
    output = io.BytesIO()
    image.save(output, "PNG", optimize=True)
    return output.getvalue()


def generate_png(env: dict[str, str], prompt: str, *, metadata: dict | None = None) -> bytes:
    key, settings = _image_settings(env)
    payload = dict(settings, prompt=prompt)
    return _image_request(key, "generations", json.dumps(payload).encode("utf-8"),
                          "application/json", settings, metadata, api_base(env))


def edit_png(env: dict[str, str], prompt: str, reference: bytes, *,
             metadata: dict | None = None) -> bytes:
    """Make a new portrait from ``prompt`` with ``reference`` (a PNG) as the input image."""
    if not reference.startswith(PNG_SIGNATURE):
        raise ValueError("The existing portrait must be a PNG image.")
    if len(reference) > MAX_REFERENCE:
        raise ValueError("The existing portrait is too large to send.")
    key, settings = _image_settings(env)
    boundary = "fmface-" + secrets.token_hex(16)
    parts = []
    for name, value in dict(settings, prompt=prompt).items():
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n'
                     f"{value}\r\n".encode("utf-8"))
    parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="image[]"; '
                 f'filename="portrait.png"\r\nContent-Type: image/png\r\n\r\n'.encode("ascii")
                 + reference + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode("ascii"))
    return _image_request(key, "edits", b"".join(parts),
                          f"multipart/form-data; boundary={boundary}", settings, metadata, api_base(env))


def _atomic_write(path: Path, content: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".fmface-", delete=False) as tmp:
        tmp.write(content)
        tmp_path = Path(tmp.name)
    try:
        tmp_path.replace(path)
    finally:
        tmp_path.unlink(missing_ok=True)


def load_graphics_maps(directory: Path) -> tuple[ET.ElementTree, ET.Element]:
    """Return config.xml and its maps list, or a new empty one."""
    config_path = directory / "config.xml"
    if not config_path.exists():
        root = ET.Element("record")
        return ET.ElementTree(root), ET.SubElement(root, "list", {"id": "maps"})
    tree = ET.parse(config_path)
    maps = tree.getroot().find("./list[@id='maps']")
    if maps is None:
        raise ValueError("Existing config.xml has no maps list; refusing to alter it.")
    return tree, maps


def graphics_maps_xml(tree: ET.ElementTree) -> bytes:
    ET.indent(tree, space="  ")
    return ET.tostring(tree.getroot(), encoding="utf-8", xml_declaration=True)


def check_graphics_config(directory: Path) -> None:
    load_graphics_maps(directory)


def save_portrait(directory: Path, player_id: str, png: bytes, *, newgen: bool | None, replace: bool) -> Path:
    player_id = validate_id(player_id)
    if not png.startswith(PNG_SIGNATURE):
        raise ValueError("Portrait must be a PNG image.")
    image_path = directory / f"{player_id}.png"
    config_path = directory / "config.xml"
    if image_path.exists() and not replace:
        raise FileExistsError(f"Portrait for ID {player_id} already exists. Tick Replace to overwrite it.")
    tree, maps = load_graphics_maps(directory)
    for record in list(maps):
        if record.tag == "record" and record.get("from") == player_id:
            maps.remove(record)
    prefixes = ("", "r-") if newgen is None else (("r-",) if newgen else ("",))
    for prefix in prefixes:
        destination = f"graphics/pictures/person/{prefix}{player_id}/portrait"
        ET.SubElement(maps, "record", {"from": player_id, "to": destination})
    xml = graphics_maps_xml(tree)
    # Roll back the image if writing the mapping fails, including during replacement.
    previous_image = image_path.read_bytes() if image_path.exists() else None
    _atomic_write(image_path, png)
    try:
        _atomic_write(config_path, xml)
    except Exception:
        try:
            if previous_image is None:
                image_path.unlink(missing_ok=True)
            else:
                _atomic_write(image_path, previous_image)
        except OSError as rollback_error:
            raise RuntimeError("Could not save config.xml or restore the previous portrait: " + str(rollback_error)) from rollback_error
        raise
    return image_path


PAGE = """<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>FM Face</title><style>body{font:16px system-ui,sans-serif;max-width:650px;margin:2rem auto;padding:0 1rem;background:#15191d;color:#f2f3f4}label{display:block;margin:1rem 0 .35rem}input,textarea,button{font:inherit}input[type=text],textarea{box-sizing:border-box;width:100%;padding:.7rem;background:#252d34;color:white;border:1px solid #5c6870;border-radius:5px}textarea{min-height:6rem}button{padding:.7rem 1rem;margin-top:1rem;cursor:pointer}small{color:#b9c2c8}img{max-width:320px;display:block;margin-top:1rem;background:#444}a{color:#8ed4ff}.message{padding:1rem;background:#26343b;border-radius:5px}</style>
<h1>FM Face</h1><p>Generate a portrait for the player currently open in FM26, or enter an ID yourself.</p>
{message}<form method="post" action="/generate"><input type="hidden" name="csrf_token" value="{csrf_token}"><label for="player_id">Player ID</label><input id="player_id" name="player_id" type="text" inputmode="numeric" required value="{player_id}"><label for="description">Appearance</label><textarea id="description" name="description" maxlength="800" required placeholder="For example: 23-year-old British-Asian man, short black hair, warm brown skin, clean-shaven">{description}</textarea><label><input type="checkbox" name="newgen" {newgen_checked}> Generated player (newgen)</label><label><input type="checkbox" name="replace"> Replace an existing portrait for this ID</label><button type="submit">Generate portrait</button></form><p><small>One click makes one billed image API request. Existing portraits are protected unless Replace is ticked.</small></p>{preview}</html>"""


class FaceHandler(BaseHTTPRequestHandler):
    env: dict[str, str] = {}
    reload_env = False
    selected_id = ""
    port = 8767
    save_lock = threading.Lock()
    form_token = secrets.token_urlsafe(32)

    def _local_host(self) -> bool:
        hosts = self.headers.get_all("Host", [])
        port = self.server.server_port
        return len(hosts) == 1 and hosts[0].lower() in (f"127.0.0.1:{port}", f"localhost:{port}")

    def _current_env(self) -> dict[str, str]:
        return load_env() if self.reload_env else self.env

    def _send(self, status: int, body: bytes, content_type: str) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _send_json(self, status: int, value: dict) -> None:
        self._send(status, json.dumps(value).encode("utf-8"), "application/json; charset=utf-8")

    def _page(self, status: int = 200, *, player_id: str = "", description: str = "", message: str = "", preview: str = "", newgen: bool = False) -> None:
        replacements = {
            "{player_id}": html.escape(player_id, quote=True),
            "{description}": html.escape(description),
            "{message}": f'<p class="message">{html.escape(message)}</p>' if message else "",
            "{preview}": preview,
            "{newgen_checked}": "checked" if newgen else "",
            "{csrf_token}": self.form_token,
        }
        page = PAGE
        for marker, value in replacements.items():
            page = page.replace(marker, value)
        body = page.encode("utf-8")
        self._send(status, body, "text/html; charset=utf-8")

    def do_GET(self) -> None:
        if not self._local_host():
            self.send_error(403, "Use the local server address")
            return
        url = urllib.parse.urlsplit(self.path)
        if url.path == "/":
            query = urllib.parse.parse_qs(url.query)
            player_id = query.get("player_id", [self.selected_id])[0]
            self._page(player_id=player_id)
            return
        match = re.fullmatch(r"/portrait/([0-9]{1,20})\.png", url.path)
        if match:
            path = output_dir(self._current_env()) / f"{match.group(1)}.png"
            if path.is_file():
                self._send(200, path.read_bytes(), "image/png")
                return
        self.send_error(404)

    def do_POST(self) -> None:
        if not self._local_host():
            self.send_error(403, "Use the local server address")
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            self.send_error(400, "Invalid Content-Length")
            return
        if length < 1 or length > MAX_BODY:
            self.send_error(413)
            return
        origin = self.headers.get("Origin", "")
        if origin and origin != f"http://{self.headers.get('Host', '')}":
            self.send_error(403)
            return
        body = self.rfile.read(length)
        if self.path == "/fm-events":
            try:
                event = json.loads(body)
                if event.get("event") != "button.click" or event.get("buttonId") != "fmface":
                    raise ValueError("Unexpected event")
                player_id = validate_id(str(event["playerId"]))
            except (ValueError, KeyError, TypeError):
                self.send_error(400)
                return
            type(self).selected_id = player_id
            self._send(204, b"", "text/plain")
            threading.Thread(target=webbrowser.open, args=(f"http://127.0.0.1:{self.port}/?player_id={player_id}",), daemon=True).start()
            return
        if self.path != "/generate":
            self.send_error(404)
            return
        try:
            fields = urllib.parse.parse_qs(body.decode("utf-8"), keep_blank_values=True)
        except UnicodeDecodeError:
            self.send_error(400, "Invalid form encoding")
            return
        player_id = fields.get("player_id", [""])[0].strip()
        description = fields.get("description", [""])[0].strip()
        newgen = "newgen" in fields
        replace = "replace" in fields
        if not hmac.compare_digest(fields.get("csrf_token", [""])[0], self.form_token):
            self._page(403, player_id=player_id, description=description,
                       message="Reload the form and try again.", newgen=newgen)
            return
        try:
            validate_id(player_id)
            env = self._current_env()
            directory = output_dir(env)
            check_graphics_config(directory)
            if (directory / f"{player_id}.png").exists() and not replace:
                raise FileExistsError("A portrait already exists for this ID. Tick Replace to overwrite it.")
            png = generate_png(env, portrait_prompt(description))
            with self.save_lock:
                save_portrait(directory, player_id, png, newgen=newgen, replace=replace)
        except (ValueError, FileExistsError, RuntimeError, OSError, ET.ParseError) as exc:
            self._page(400, player_id=player_id, description=description, message=str(exc), newgen=newgen)
            return
        preview = f'<p>Saved to {html.escape(str(directory))}</p><img src="/portrait/{player_id}.png" alt="Generated portrait">'
        self._page(player_id=player_id, description=description, message="Portrait saved. Restart FM26 to load the new graphic.", preview=preview, newgen=newgen)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    serve = sub.add_parser("serve", help="Run the local web UI and FM ActionLink receiver")
    serve.add_argument("--port", type=int, default=8767)
    imp = sub.add_parser("import", help="Install an existing PNG without calling the image API")
    imp.add_argument("--player-id", required=True)
    imp.add_argument("--png", type=Path, required=True)
    imp.add_argument("--newgen", action="store_true")
    imp.add_argument("--replace", action="store_true")
    args = parser.parse_args()
    env = load_env()
    if args.command == "import":
        path = save_portrait(output_dir(env), args.player_id, args.png.read_bytes(), newgen=args.newgen, replace=args.replace)
        print(f"Saved {path}")
    else:
        FaceHandler.env = env
        FaceHandler.reload_env = True
        FaceHandler.port = args.port
        server = ThreadingHTTPServer(("127.0.0.1", args.port), FaceHandler)
        print(f"FM Face listening at http://127.0.0.1:{args.port}/", flush=True)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            server.server_close()


if __name__ == "__main__":
    try:
        main()
    except (ValueError, FileExistsError, OSError, ET.ParseError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        sys.exit(1)
