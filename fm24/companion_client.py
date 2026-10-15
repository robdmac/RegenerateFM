"""In-process requests to the companion's generation endpoint, shared by menus."""

from __future__ import annotations

import json
import secrets
import time
import urllib.error
import urllib.request


def request_generation(port: int, token: str, handler, *, expected_id: str | None = None,
                       mode: str = "generate") -> dict:
    """Generate for the open profile, optionally only if it is ``expected_id``.

    ``mode`` "update" refreshes the person's existing portrait instead.

    Reuses the same nonce, lock, profile validation, and graphics checks as the
    HTTP callback. This request stays entirely on loopback.
    """
    nonce = secrets.token_urlsafe(24)
    with handler.lock:
        handler.nonces[nonce] = time.monotonic()
    body = json.dumps({"key": token, "nonce": nonce,
                       "expectedPlayerId": expected_id, "mode": mode}).encode()
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}/fm24/generate", data=body,
        headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=240) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        try:
            return json.load(error)
        except (ValueError, OSError):
            return {"ok": False, "error": f"Local companion returned HTTP {error.code}."}
    except (urllib.error.URLError, OSError) as error:
        return {"ok": False, "error": f"Could not reach the local companion: {error}"}


def describe_result(result: dict) -> str:
    """One terminal message for any single-portrait result."""
    if not result.get("ok"):
        return f"FM24 generation failed: {result.get('error', 'unknown error')}"
    if result.get("preview"):
        source = f"\nExisting portrait: {result['reference']}" if result.get("reference") else ""
        return f"FM24 profile preview for person {result.get('playerId')}:\n{result.get('prompt', '')}{source}"
    if result.get("refreshed"):
        return f"FM24 portrait loaded for person {result.get('playerId')}."
    return f"FM24 portrait saved; {result.get('warning', 'reload needs attention')}"
