import base64
import io
import json
import os
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import credits  # noqa: E402
import keystore  # noqa: E402
import regeneratefm  # noqa: E402

TOKEN = "rfm_" + "t" * 43


def _png() -> str:
    from PIL import Image
    buffer = io.BytesIO()
    Image.new("RGBA", (1024, 1024), (90, 90, 100, 255)).save(buffer, "PNG")
    return base64.b64encode(buffer.getvalue()).decode()


class FakeCredits:
    """A local stand-in for the credits Worker: device sign-in, models and images."""

    def __init__(self, pending=1, link_host=None, expire=False):
        fake = self
        self.pending, self.expire, self.requests = pending, expire, []

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def _send(self, status, value):
                body = json.dumps(value).encode()
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def _record(self):
                fake.requests.append((self.command, self.path, self.headers.get("User-Agent"),
                                      self.headers.get("Authorization")))

            def do_GET(self):
                self._record()
                if self.path == "/v1/models":
                    if self.headers.get("Authorization") != f"Bearer {TOKEN}":
                        return self._send(401, {"error": {"message": "Sign in again."}})
                    return self._send(200, {"data": []})
                self._send(404, {})

            def do_POST(self):
                self._record()
                length = int(self.headers.get("Content-Length", "0"))
                body = json.loads(self.rfile.read(length) or b"{}")
                if self.path == "/device/start":
                    base = link_host or fake.url
                    return self._send(200, {"device_code": "d" * 43, "user_code": "WXYZ-K7P3",
                                            "verification_uri_complete": f"{base}/link?code=WXYZ-K7P3",
                                            "expires_in": 600, "interval": 2})
                if self.path == "/device/poll":
                    if fake.expire:
                        return self._send(410, {"error": {"message": "expired"}})
                    if fake.pending:
                        fake.pending -= 1
                        return self._send(200, {"status": "pending", "interval": 2})
                    return self._send(200, {"status": "approved", "token": TOKEN,
                                            "email": "player@example.com", "credits": 75})
                if self.path == "/v1/images/generations":
                    fake.prompt = body.get("prompt")
                    return self._send(200, {"data": [{"b64_json": _png()}]})
                self._send(404, {})

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()


class CreditsTests(unittest.TestCase):
    def setUp(self):
        self.fake = FakeCredits()
        self.addCleanup(self.fake.close)

    def test_device_sign_in_waits_until_approved(self):
        sign_in = credits.start(self.fake.url)
        self.assertEqual(sign_in.user_code, "WXYZ-K7P3")
        self.assertTrue(sign_in.link.startswith(self.fake.url + "/link"))
        sleeps = []
        answer = credits.wait(self.fake.url, sign_in, sleep=sleeps.append)
        self.assertEqual((answer["token"], answer["credits"]), (TOKEN, 75))
        self.assertEqual(sleeps, [2, 2])
        # Cloudflare refuses Python's own user agent.
        self.assertEqual({agent for _, _, agent, _ in self.fake.requests}, {credits.USER_AGENT})

    def test_an_expired_sign_in_ends_the_wait(self):
        self.fake.expire = True
        self.assertIsNone(credits.wait(self.fake.url, credits.start(self.fake.url), sleep=lambda _: None))

    def test_a_link_to_another_site_is_refused(self):
        other = FakeCredits(link_host="https://example.com")
        self.addCleanup(other.close)
        with self.assertRaisesRegex(RuntimeError, "unexpected sign-in link"):
            credits.start(other.url)

    def test_service_url_is_https_or_this_computer(self):
        self.assertEqual(credits.service_url(""), credits.DEFAULT_SERVICE)
        self.assertEqual(credits.service_url(" http://127.0.0.1:9/ "), "http://127.0.0.1:9")
        with self.assertRaises(ValueError):
            credits.service_url("http://example.com")

    def test_tokens_are_saved_and_checked_with_the_service(self):
        self.assertEqual(keystore.clean(f" {TOKEN} "), TOKEN)
        base = credits.api_base(self.fake.url)
        self.assertIsNone(keystore.check(TOKEN, base=base))
        self.assertEqual(keystore.check("rfm_" + "x" * 43, base=base), keystore.SIGNED_OUT)

    def test_a_token_makes_faces_through_the_service(self):
        env = {"OPENAI_API_KEY": TOKEN, "REGENERATEFM_CREDITS_URL": self.fake.url,
               "OPENAI_BASE_URL": "http://127.0.0.1:1/v1"}
        self.assertEqual(regeneratefm.api_base(env, TOKEN), self.fake.url + "/v1")
        self.assertEqual(regeneratefm.api_base(env, "sk-" + "a" * 40), "http://127.0.0.1:1/v1")
        self.assertTrue(regeneratefm.generate_png(env, "a face").startswith(regeneratefm.PNG_SIGNATURE))
        self.assertIn(("POST", "/v1/images/generations", credits.USER_AGENT, f"Bearer {TOKEN}"),
                      self.fake.requests)


class CompanionCreditsTests(unittest.TestCase):
    """Buy credits / Sign in in FM24's key prompt."""

    def setUp(self):
        sys.path.insert(0, str(ROOT / "fm24"))
        import companion
        self.companion = companion
        self.handler = type("Handler", (), {"preview_only": False, "reload_port": 8770, "token": "t" * 43,
                                            "key_lock": companion.threading.Lock(), "key_rejected": False})
        self.fake = FakeCredits()
        self.addCleanup(self.fake.close)

    def test_the_prompts_share_one_answer_for_buy_credits(self):
        import key_dialog
        self.assertEqual(key_dialog.CREDITS_CHOICE, self.companion.CREDITS_CHOICE)
        prompt = (ROOT / "fm24" / "native_menu" / "key_prompt_macos.m").read_text()
        self.assertIn(f'CREDITS_CHOICE = "{self.companion.CREDITS_CHOICE}"', prompt)
        self.assertNotRegex(self.companion.CREDITS_CHOICE, keystore.KEY_RE)

    def test_buy_credits_signs_in_and_saves_the_token(self):
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "RegenerateFM" / "openai-key"

            def load_env(use_key_file=True):
                env = {"REGENERATEFM_CREDITS_URL": self.fake.url}
                if use_key_file and store.exists():
                    env["OPENAI_API_KEY"] = keystore.load(store)
                return env
            with mock.patch.object(keystore, "default_path", return_value=store), \
                    mock.patch.object(self.companion, "load_env", side_effect=load_env), \
                    mock.patch.object(sys, "platform", "darwin"), \
                    mock.patch.object(self.companion, "ask_key", return_value=self.companion.CREDITS_CHOICE) as asked, \
                    mock.patch.object(self.companion, "show_dialog") as told, \
                    mock.patch.object(credits.time, "sleep"), \
                    mock.patch("webbrowser.open", return_value=True) as browser, \
                    mock.patch("builtins.print"):
                self.assertTrue(self.companion.ensure_key(self.handler))
                self.assertTrue(self.companion.ensure_key(self.handler))  # signed in: no second prompt
            self.assertEqual(asked.call_count, 1)
            self.assertIn("Buy credits / Sign in", asked.call_args.args[2])
            browser.assert_called_once_with(self.fake.url + "/link?code=WXYZ-K7P3")
            messages = [call.args[4] for call in told.call_args_list]
            self.assertIn("WXYZ-K7P3", messages[0])
            self.assertIn("player@example.com, with 75 credits", messages[1])
            self.assertEqual(keystore.load(store), TOKEN)

    def test_a_failed_sign_in_asks_again_with_the_reason(self):
        self.fake.expire = True
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "openai-key"
            with mock.patch.object(keystore, "default_path", return_value=store), \
                    mock.patch.object(self.companion, "load_env",
                                      return_value={"REGENERATEFM_CREDITS_URL": self.fake.url}), \
                    mock.patch.object(sys, "platform", "darwin"), \
                    mock.patch.object(self.companion, "ask_key",
                                      side_effect=[self.companion.CREDITS_CHOICE, None]) as asked, \
                    mock.patch.object(self.companion, "show_dialog"), \
                    mock.patch.object(credits.time, "sleep"), \
                    mock.patch("webbrowser.open", return_value=True), \
                    mock.patch("builtins.print"):
                self.assertFalse(self.companion.ensure_key(self.handler))
            self.assertIn("sign-in expired", asked.call_args_list[1].args[2])
            self.assertFalse(store.exists())

    def test_an_ended_sign_in_is_named_at_the_prompt(self):
        with mock.patch.object(self.companion, "load_env", return_value={"OPENAI_API_KEY": TOKEN}):
            self.assertIn("credits sign-in has ended", self.companion._rejected_text())
        with mock.patch.object(self.companion, "load_env", return_value={"OPENAI_API_KEY": "sk-" + "a" * 40}):
            self.assertIn("didn't accept the saved key", self.companion._rejected_text())


if __name__ == "__main__":
    unittest.main()
