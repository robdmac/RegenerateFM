import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import fmface  # noqa: E402
import keystore  # noqa: E402

KEY = "sk-test_" + "a" * 40


class KeyStoreTests(unittest.TestCase):
    def test_clean_accepts_keys_and_explains_mistakes(self):
        self.assertEqual(keystore.clean(f"  {KEY}\n"), KEY)
        with self.assertRaisesRegex(ValueError, "Paste"):
            keystore.clean("  ")
        with self.assertRaisesRegex(ValueError, "starts with sk-"):
            keystore.clean("hello")
        with self.assertRaisesRegex(ValueError, "starts with sk-"):
            keystore.clean("sk-abc def ghi jkl mno pqr")

    @unittest.skipIf(sys.platform == "win32", "plain format is used off Windows")
    def test_plain_round_trip_matches_the_plugin_format(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "FM Face" / "openai-key"
            self.assertIsNone(keystore.load(path))
            keystore.save(path, KEY)
            self.assertEqual(path.read_bytes(), b"FMFK0\n" + KEY.encode())
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            self.assertEqual(keystore.load(path), KEY)
            self.assertFalse(path.with_name("openai-key.tmp").exists())

    def test_rejects_unknown_and_foreign_encrypted_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "openai-key"
            path.write_bytes(KEY.encode())
            with self.assertRaisesRegex(ValueError, "format"):
                keystore.load(path)
            if sys.platform != "win32":
                path.write_bytes(b"FMFK1\n\x01\x02")
                with self.assertRaisesRegex(ValueError, "encrypted for Windows"):
                    keystore.load(path)

    def test_load_env_falls_back_to_the_key_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "openai-key"
            store.write_bytes(b"FMFK0\n" + KEY.encode())
            env_file = Path(tmp) / ".env"
            env_file.write_text("OPENAI_IMAGE_QUALITY=low\n", encoding="utf-8")
            with mock.patch.object(keystore, "default_path", return_value=store), \
                    mock.patch.dict(os.environ, {}, clear=True):
                self.assertEqual(fmface.load_env(env_file)["OPENAI_API_KEY"], KEY)
                env_file.write_text("OPENAI_API_KEY=sk-from-env\n", encoding="utf-8")
                self.assertEqual(fmface.load_env(env_file)["OPENAI_API_KEY"], "sk-from-env")


class ApiBaseTests(unittest.TestCase):
    def test_openai_by_default_and_http_only_on_this_computer(self):
        self.assertEqual(keystore.api_base(""), "https://api.openai.com/v1")
        self.assertEqual(keystore.api_base(" http://127.0.0.1:8899/v1/ "), "http://127.0.0.1:8899/v1")
        self.assertEqual(keystore.api_base("https://proxy.example/v1"), "https://proxy.example/v1")
        for unsafe in ("http://192.168.1.5:8899/v1", "ftp://127.0.0.1/v1", "api.openai.com"):
            with self.assertRaises(ValueError):
                keystore.api_base(unsafe)
        self.assertEqual(fmface.api_base({"OPENAI_BASE_URL": "http://localhost:1/v1"}), "http://localhost:1/v1")


class CompanionKeyPromptTests(unittest.TestCase):
    def setUp(self):
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "fm24"))
        import companion
        self.companion = companion

    def prompt(self, store, answers, problems=(), env_key="", saved_key=""):
        tty = mock.Mock(isatty=mock.Mock(return_value=True))
        def load_env(use_key_file=True):
            key = env_key or (saved_key if use_key_file else "")
            return {"OPENAI_API_KEY": key} if key else {}
        with mock.patch.object(keystore, "default_path", return_value=store), \
                mock.patch.object(self.companion, "load_env", side_effect=load_env), \
                mock.patch.object(sys, "stdin", tty), \
                mock.patch("getpass.getpass", side_effect=answers) as asked, \
                mock.patch.object(keystore, "check", side_effect=list(problems) + [None]) as checked, \
                mock.patch("builtins.print"):
            self.companion.ask_for_key()
        return asked.call_count, checked.call_count

    def test_asks_again_until_a_key_is_accepted_then_saves_it(self):
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "FM Face" / "openai-key"
            asked, checked = self.prompt(store, ["hello", "sk-wrong_" + "c" * 30, f" {KEY} "],
                                         problems=["OpenAI didn't accept this key."])
            self.assertEqual((asked, checked), (3, 2))
            self.assertEqual(keystore.load(store), KEY)

    def test_an_empty_answer_skips_without_saving(self):
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "openai-key"
            self.assertEqual(self.prompt(store, [""]), (1, 0))
            self.assertFalse(store.exists())

    def test_a_saved_key_is_rechecked_and_replaced_only_when_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "openai-key"
            self.assertEqual(self.prompt(store, [], saved_key=KEY), (0, 1))
            self.assertEqual(self.prompt(store, [], problems=["Could not reach OpenAI: offline"], saved_key=KEY),
                             (0, 1))
            asked, checked = self.prompt(store, [KEY], problems=[keystore.REJECTED], saved_key="sk-old_" + "d" * 30)
            self.assertEqual((asked, checked), (1, 2))
            self.assertEqual(keystore.load(store), KEY)

    def test_a_key_from_env_is_left_alone(self):
        self.assertEqual(self.prompt(Path("unused"), [], env_key=KEY), (0, 0))

    def test_rejected_key_errors_say_how_to_replace_it(self):
        self.assertIn("start it again", self.companion.error_text(RuntimeError("OpenAI image API returned HTTP 401: x")))
        self.assertEqual(self.companion.error_text(RuntimeError("HTTP 500")), "HTTP 500")

    def test_no_prompt_without_a_key_file_location(self):
        self.assertEqual(self.prompt(None, []), (0, 0))


class CompanionInGameKeyTests(unittest.TestCase):
    """FM24 for macOS asks for the key in the game (the library's key prompt)."""

    def setUp(self):
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "fm24"))
        import companion
        self.companion = companion
        self.handler = type("Handler", (), {"preview_only": False, "reload_port": 8770, "token": "t" * 43,
                                            "key_lock": companion.threading.Lock(), "key_rejected": False})

    def ensure(self, store, answers, problems=(), env_key="", saved_key="", platform="darwin"):
        def load_env(use_key_file=True):
            key = env_key or (saved_key if use_key_file else "")
            return {"OPENAI_API_KEY": key} if key else {}
        with mock.patch.object(keystore, "default_path", return_value=store), \
                mock.patch.object(self.companion, "load_env", side_effect=load_env), \
                mock.patch.object(sys, "platform", platform), \
                mock.patch.object(self.companion, "ask_key", side_effect=answers) as asked, \
                mock.patch.object(keystore, "check", side_effect=list(problems) + [None]) as checked, \
                mock.patch("builtins.print"):
            ready = self.companion.ensure_key(self.handler)
        return ready, asked, checked.call_count

    def test_asks_in_game_until_a_key_is_accepted_then_saves_it(self):
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "FM Face" / "openai-key"
            ready, asked, checked = self.ensure(store, ["hello", "sk-wrong_" + "c" * 30, f" {KEY} "],
                                                problems=["OpenAI didn't accept this key."])
            self.assertEqual((ready, asked.call_count, checked), (True, 3, 2))
            self.assertEqual(keystore.load(store), KEY)
            # Each retry says what was wrong with the last answer.
            self.assertIn("starts with sk-", asked.call_args_list[1].args[2])
            self.assertIn("didn't accept", asked.call_args_list[2].args[2])

    def test_cancel_saves_nothing(self):
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "openai-key"
            ready, asked, checked = self.ensure(store, [None])
            self.assertEqual((ready, asked.call_count, checked), (False, 1, 0))
            self.assertFalse(store.exists())

    def test_no_prompt_with_a_key_or_off_macos(self):
        store = Path("unused")
        self.assertEqual(self.ensure(store, [], env_key=KEY)[1].call_count, 0)
        self.assertEqual(self.ensure(store, [], saved_key=KEY)[1].call_count, 0)
        self.assertEqual(self.ensure(store, [], platform="win32")[1].call_count, 0)

    def test_a_companion_fm24_started_asks_in_its_key_dialog(self):
        import key_dialog
        self.handler.started_by_fm24 = 4242
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "FM Face" / "openai-key"
            with mock.patch.object(keystore, "default_path", return_value=store), \
                    mock.patch.object(self.companion, "load_env",
                                      side_effect=lambda use_key_file=True: {"OPENAI_API_KEY": keystore.load(store)}
                                      if use_key_file and store.exists() else {}), \
                    mock.patch.object(self.companion, "_asks_in_window", return_value=True), \
                    mock.patch.object(self.companion, "show_dialog") as told, \
                    mock.patch.object(key_dialog, "ask", side_effect=["hello", f" {KEY} "]) as asked, \
                    mock.patch.object(keystore, "check", return_value=None), \
                    mock.patch("builtins.print"):
                self.assertTrue(self.companion.ensure_key(self.handler))
                # FM24 says where to look; a saved key needs no second dialog.
                self.assertIn("OpenAI key window", told.call_args.args[4])
                self.assertTrue(self.companion.ensure_key(self.handler))
            self.assertEqual(asked.call_count, 2)
            self.assertIn("starts with sk-", asked.call_args_list[1].args[0])
            self.assertEqual(keystore.load(store), KEY)
        with mock.patch.object(sys, "platform", "win32"), \
                mock.patch.object(self.companion, "load_env", return_value={}):
            message = self.companion.error_text(RuntimeError("OpenAI image API returned HTTP 401: x"), self.handler)
        self.assertIn("face command again", message)

    def test_closing_the_key_dialog_saves_nothing(self):
        import key_dialog
        self.handler.started_by_fm24 = 4242
        with mock.patch.object(keystore, "default_path", return_value=Path("unused")), \
                mock.patch.object(self.companion, "load_env", return_value={}), \
                mock.patch.object(self.companion, "_asks_in_window", return_value=True), \
                mock.patch.object(self.companion, "show_dialog"), \
                mock.patch.object(key_dialog, "ask", return_value=None), \
                mock.patch.object(keystore, "save") as saved, \
                mock.patch("builtins.print"):
            self.assertFalse(self.companion.ensure_key(self.handler))
        saved.assert_not_called()

    def test_a_rejected_saved_key_is_asked_for_again(self):
        with mock.patch.object(sys, "platform", "darwin"), \
                mock.patch.object(self.companion, "load_env", return_value={}):
            message = self.companion.error_text(RuntimeError("OpenAI image API returned HTTP 401: x"), self.handler)
        self.assertIn("face command again", message)
        self.assertTrue(self.handler.key_rejected)
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "openai-key"
            ready, asked, _ = self.ensure(store, [KEY], saved_key="sk-old_" + "d" * 30)
            self.assertTrue(ready)
            self.assertIn("didn't accept the saved key", asked.call_args.args[2])
            self.assertFalse(self.handler.key_rejected)
            self.assertEqual(keystore.load(store), KEY)


if __name__ == "__main__":
    unittest.main()
