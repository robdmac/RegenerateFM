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


if __name__ == "__main__":
    unittest.main()
