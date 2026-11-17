import shutil
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent / "tools"))
import sign_builds  # noqa: E402


class SignBuildsTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which("openssl"), "needs openssl")
    def test_the_signed_list_is_builds_json_signed_with_the_release_key(self):
        sign_builds.check()

    def test_a_build_needs_its_hashes_system_and_user_data_folder(self):
        good = {"name": "FM27 27.1.0 (Steam)", "os": "windows", "game_assembly": "a" * 64, "game_plugin": "b" * 64,
                "user_data": "Football Manager 27"}
        sign_builds.check_build(good)
        for field, value in (("os", "linux"), ("game_assembly", "A" * 64), ("game_plugin", "b" * 63),
                             ("user_data", "Football Manager 2024"), ("name", " ")):
            with self.subTest(field=field), self.assertRaises(SystemExit):
                sign_builds.check_build({**good, field: value})

    def test_a_game_folder_is_read(self):
        import tempfile
        with tempfile.TemporaryDirectory() as name:
            folder = Path(name)
            (folder / "fm_Data" / "Plugins" / "x86_64").mkdir(parents=True)
            (folder / "GameAssembly.dll").write_bytes(b"assembly")
            (folder / "fm_Data" / "Plugins" / "x86_64" / "game_plugin.dll").write_bytes(b"plugin")
            (folder / "fm_Data" / "app.info").write_text("Sports Interactive\nFootball Manager 27", encoding="utf-8")
            build = sign_builds.game_folder(folder)
            self.assertEqual(build["os"], "windows")
            self.assertEqual(build["user_data"], "Football Manager 27")
            self.assertEqual(build["game_assembly"], sign_builds.file_hash(folder / "GameAssembly.dll"))
            sign_builds.check_build({"name": "x", **build})


if __name__ == "__main__":
    unittest.main()
