"""The player's OpenAI key, kept outside the game and RegenerateFM folders.

On Windows it is encrypted with DPAPI for the current Windows user, in
%APPDATA%\\RegenerateFM\\openai-key. On macOS it is in ~/Library/Application
Support/RegenerateFM/openai-key, readable only by the user. The FM26 plugin
(hotreload/FaceGenerator.cs, KeyStore) reads and writes the same file in the
same format, so the plugin and regeneratefm.py share one key.
"""

from __future__ import annotations

import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

PROTECTED = b"RFMK1\n"
PLAIN = b"RFMK0\n"
ENTROPY = b"RegenerateFM OpenAI API key"
KEY_RE = re.compile(r"sk-[A-Za-z0-9_\-]{16,300}")
KEYS_PAGE = "https://platform.openai.com/api-keys"
DEFAULT_API = "https://api.openai.com/v1"
REJECTED = "OpenAI didn't accept this key. Check that you copied all of it."


def default_path() -> Path | None:
    """The key file on Windows and macOS, or None on Linux (which keeps the key in .env)."""
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "RegenerateFM" / "openai-key"
    if sys.platform != "win32":
        return None
    appdata = os.environ.get("APPDATA")
    return Path(appdata) / "RegenerateFM" / "openai-key" if appdata else None


def clean(key: str) -> str:
    """The key without surrounding spaces, or ValueError saying what is wrong."""
    key = (key or "").strip()
    if not key:
        raise ValueError("Paste your OpenAI API key first.")
    if not KEY_RE.fullmatch(key):
        raise ValueError("That doesn't look like an OpenAI API key. It starts with sk- and has no spaces.")
    return key


def load(path: Path) -> str | None:
    try:
        data = path.read_bytes()
    except FileNotFoundError:
        return None
    if data.startswith(PROTECTED):
        payload = _dpapi(data[len(PROTECTED):], protect=False)
    elif data.startswith(PLAIN):
        payload = data[len(PLAIN):]
    else:
        raise ValueError(f"The saved OpenAI key file is not in RegenerateFM's format: {path}")
    return payload.decode("utf-8").strip() or None


def save(path: Path, key: str) -> None:
    payload = clean(key).encode("utf-8")
    data = PROTECTED + _dpapi(payload, protect=True) if sys.platform == "win32" else PLAIN + payload
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    try:
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "wb") as handle:
            handle.write(data)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def api_base(configured: str = "") -> str:
    """The image API address: OpenAI's, or OPENAI_BASE_URL for testing. Plain
    http only for this computer, so a key is never sent unencrypted."""
    base = (configured or "").strip().rstrip("/") or DEFAULT_API
    parts = urllib.parse.urlsplit(base)
    if parts.scheme == "https" and parts.hostname:
        return base
    if parts.scheme == "http" and parts.hostname in ("127.0.0.1", "localhost", "::1"):
        return base
    raise ValueError("OPENAI_BASE_URL must be an https address, or http on this computer (127.0.0.1).")


def check(key: str, timeout: float = 30, base: str = DEFAULT_API) -> str | None:
    """None if OpenAI accepts the key, otherwise the problem. Listing models is free."""
    request = urllib.request.Request(f"{base}/models",
                                     headers={"Authorization": f"Bearer {key}"})
    try:
        with urllib.request.urlopen(request, timeout=timeout):
            return None
    except urllib.error.HTTPError as exc:
        if exc.code == 401:
            return REJECTED
        try:
            detail = json.loads(exc.read(4096)).get("error", {}).get("message", "")
        except (ValueError, AttributeError):
            detail = ""
        return f"OpenAI returned HTTP {exc.code}: {str(detail or exc.reason).replace(key, '[REDACTED]')}"
    except (urllib.error.URLError, TimeoutError) as exc:
        return f"Could not reach OpenAI: {exc.reason if isinstance(exc, urllib.error.URLError) else exc}"


def _dpapi(data: bytes, protect: bool) -> bytes:
    if sys.platform != "win32":
        raise ValueError("The saved OpenAI key is encrypted for Windows and can't be read here.")
    import ctypes
    from ctypes import wintypes

    class Blob(ctypes.Structure):
        _fields_ = [("size", wintypes.DWORD), ("data", ctypes.POINTER(ctypes.c_char))]

    def blob(value: bytes) -> tuple[Blob, ctypes.Array]:
        buffer = ctypes.create_string_buffer(value, len(value))
        return Blob(len(value), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_char))), buffer

    source, _source_buffer = blob(data)
    entropy, _entropy_buffer = blob(ENTROPY)
    result = Blob()
    crypt32 = ctypes.windll.crypt32
    ui_forbidden = 0x1
    if protect:
        ok = crypt32.CryptProtectData(ctypes.byref(source), "RegenerateFM OpenAI API key", ctypes.byref(entropy),
                                      None, None, ui_forbidden, ctypes.byref(result))
    else:
        ok = crypt32.CryptUnprotectData(ctypes.byref(source), None, ctypes.byref(entropy),
                                        None, None, ui_forbidden, ctypes.byref(result))
    if not ok:
        raise ValueError(f"Windows could not {'encrypt' if protect else 'decrypt'} the OpenAI key "
                         f"(error {ctypes.GetLastError()}).")
    try:
        return ctypes.string_at(result.data, result.size)
    finally:
        ctypes.windll.kernel32.LocalFree(result.data)
