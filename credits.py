"""RegenerateFM credits for the FM24 companion and regeneratefm.py, like the
FM26 plugin's Credits (hotreload/FaceGenerator.cs).

A credits token ("rfm_…") is bought instead of an OpenAI key. The player
gets one by device sign-in: the companion starts a sign-in, opens the browser
at the returned link, and polls until the player has signed in with their
email and confirmed the code; the token is then saved where an OpenAI key
goes. Image requests with a token go to the credits service, which speaks
OpenAI's image API and takes one credit a face.
"""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass

PREFIX = "rfm_"
DEFAULT_SERVICE = "https://credits.regenerate.fm"
# Cloudflare turns away Python's own user agent ("Python-urllib/3.x").
USER_AGENT = "RegenerateFM"
BUY_TEXT = ("Buy RegenerateFM credits: one credit makes or updates one face. Packs go from 75 credits "
            "for £2 / €2.50 / $2.50 to 1000 for £20 / €24 / $24. You sign in with your email in your "
            "browser; there's nothing to paste.")


def is_token(key: str | None) -> bool:
    return bool(key) and key.startswith(PREFIX)


def service_url(configured: str = "") -> str:
    """REGENERATEFM_CREDITS_URL for testing, else the credits service. Plain
    http only for this computer, so a token is never sent unencrypted."""
    url = (configured or "").strip().rstrip("/") or DEFAULT_SERVICE
    parts = urllib.parse.urlsplit(url)
    if parts.scheme == "https" and parts.hostname:
        return url
    if parts.scheme == "http" and parts.hostname in ("127.0.0.1", "localhost", "::1"):
        return url
    raise ValueError("REGENERATEFM_CREDITS_URL must be an https address, or http on this computer (127.0.0.1).")


def api_base(service: str) -> str:
    """The image API address for a token: the credits service's."""
    return service.rstrip("/") + "/v1"


@dataclass
class SignIn:
    device_code: str
    user_code: str
    link: str
    interval: int
    expires: float  # time.monotonic() deadline


def _post(url: str, value: dict, timeout: float) -> tuple[int, dict]:
    request = urllib.request.Request(url, data=json.dumps(value).encode("utf-8"), method="POST",
                                     headers={"Content-Type": "application/json", "User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as exc:
        try:
            body = json.loads(exc.read(4096))
        except ValueError:
            body = {}
        return exc.code, body if isinstance(body, dict) else {}


def start(service: str, *, timeout: float = 20) -> SignIn:
    """Start a device sign-in. Raises RuntimeError if the service can't."""
    service = service.rstrip("/")
    try:
        status, body = _post(service + "/device/start", {}, timeout)
    except (urllib.error.URLError, TimeoutError, ValueError) as exc:
        raise RuntimeError(f"Couldn't reach RegenerateFM credits: {getattr(exc, 'reason', exc)}") from exc
    if status != 200:
        raise RuntimeError(f"RegenerateFM credits couldn't start a sign-in (HTTP {status}).")
    try:
        link = str(body["verification_uri_complete"])
        sign_in = SignIn(str(body["device_code"]), str(body["user_code"]), link,
                         max(2, int(body["interval"])), time.monotonic() + int(body["expires_in"]))
    except (KeyError, TypeError, ValueError) as exc:
        raise RuntimeError("RegenerateFM credits sent an unexpected answer.") from exc
    # Only the service's own pages are opened in the browser.
    if not link.startswith(service + "/"):
        raise RuntimeError("RegenerateFM credits returned an unexpected sign-in link.")
    return sign_in


def poll(service: str, device_code: str, *, timeout: float = 20) -> dict:
    """One check of a sign-in: {"status": "pending" | "slow_down" | "expired"},
    or {"status": "approved", "token", "email", "credits"}."""
    try:
        status, body = _post(service.rstrip("/") + "/device/poll", {"device_code": device_code}, timeout)
    except (urllib.error.URLError, TimeoutError, ValueError) as exc:
        raise RuntimeError(f"Couldn't reach RegenerateFM credits: {getattr(exc, 'reason', exc)}") from exc
    if status == 410:
        return {"status": "expired"}
    if status != 200 or body.get("status") not in ("pending", "slow_down", "approved", "expired"):
        raise RuntimeError(f"RegenerateFM credits answered HTTP {status}.")
    if body["status"] == "approved" and not is_token(body.get("token")):
        raise RuntimeError("RegenerateFM credits sent an unexpected answer.")
    return body


def wait(service: str, sign_in: SignIn, *, cancelled=lambda: False, sleep=time.sleep,
         clock=time.monotonic) -> dict | None:
    """Poll until the player has signed in: the approved answer, or None if
    the sign-in expired or ``cancelled()`` became true."""
    interval = sign_in.interval
    while clock() < sign_in.expires and not cancelled():
        sleep(interval)
        answer = poll(service, sign_in.device_code)
        if answer["status"] == "approved":
            return answer
        if answer["status"] == "expired":
            return None
        if answer["status"] == "slow_down":
            interval += 2
    return None
