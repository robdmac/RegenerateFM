"""FM24 one-step setup: finding the game, token reuse and the set-up check (no game needed)."""
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "fm24"))
import setup_fm24  # noqa: E402

TOKEN = "t" * 43


class SetupTests(unittest.TestCase):
    def setUp(self):
        # The fixtures are Linux Steam libraries with fm.exe and Proton prefixes, on any system.
        setup_fm24.native_install.use_system(False)
        self.addCleanup(setup_fm24.native_install.use_system, sys.platform == "darwin")
        platform = mock.patch.object(setup_fm24.sys, "platform", "linux")
        platform.start()
        self.addCleanup(platform.stop)
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def library(self, name: str) -> Path:
        library = self.root / name
        game = library / "steamapps" / "common" / "Football Manager 2024"
        game.mkdir(parents=True)
        (game / "fm.exe").write_bytes(b"MZ")
        return library

    def test_finds_fm24_in_any_steam_library(self):
        steam = self.root / "steam"
        (steam / "steamapps").mkdir(parents=True)
        other = self.library("games")
        (steam / "steamapps" / "libraryfolders.vdf").write_text(
            '"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"%s"\n\t}\n\t"1"\n\t{\n\t\t"path"\t\t"%s"\n\t}\n}\n'
            % (steam, other), encoding="utf-8")
        with mock.patch.object(setup_fm24.steam_launch, "steam_roots", return_value=[steam]):
            self.assertEqual(setup_fm24.steam_libraries(), [steam.resolve(), other.resolve()])
            self.assertEqual(setup_fm24.find_game_dir(),
                             other.resolve() / "steamapps" / "common" / "Football Manager 2024")

    @unittest.skipIf(sys.platform == "win32", "Proton layout")
    def test_user_data_is_in_the_proton_prefix(self):
        game = self.library("lib") / "steamapps" / "common" / "Football Manager 2024"
        self.assertIsNone(setup_fm24.find_user_data_dir(game))
        user_data = (self.root / "lib" / "steamapps" / "compatdata" / "2252570" / "pfx" / "drive_c" / "users" /
                     "steamuser" / "Documents" / "Sports Interactive" / "Football Manager 2024")
        user_data.mkdir(parents=True)
        self.assertEqual(setup_fm24.find_user_data_dir(game), user_data)

    def test_installed_token_is_read_from_the_dll_settings(self):
        self.assertIsNone(setup_fm24.installed_token(self.root))
        (self.root / "fmface24.ini").write_text(f"[fmface]\nenabled=1\nport=8769\ntoken={TOKEN}\n", encoding="ascii")
        self.assertEqual(setup_fm24.installed_token(self.root), TOKEN)

    def test_setup_keeps_the_token_fm24_already_has(self):
        game, user_data = self.root / "game", self.root / "user"
        game.mkdir()
        user_data.mkdir()
        (game / "fmface24.ini").write_text(f"token={TOKEN}\n", encoding="ascii")
        dll = self.root / "winmm.dll"
        dll.write_bytes(b"MZ")
        auth = self.root / "new-folder" / ".fm24-auth"
        with mock.patch.object(setup_fm24, "bundled_dll", return_value=dll), \
                mock.patch.object(setup_fm24.native_install, "game_running", return_value=False), \
                mock.patch.object(setup_fm24, "build_skin_from_game") as build, \
                mock.patch.object(setup_fm24.native_install, "main") as install, \
                mock.patch("builtins.print"):
            setup_fm24.setup(game, user_data, auth)
        self.assertEqual(auth.read_text(encoding="ascii").strip(), TOKEN)
        build.assert_called_once_with(game, user_data / "skins" / "fmface")
        self.assertIn(str(auth), install.call_args.args[0])
        self.assertTrue((user_data / "graphics" / "fmface").is_dir())

    def test_refuses_while_fm24_runs(self):
        with mock.patch.object(setup_fm24, "is_set_up", return_value=False), \
                mock.patch.object(setup_fm24.native_install, "game_running", return_value=True):
            with self.assertRaisesRegex(SystemExit, "Close FM24"):
                setup_fm24.setup(self.root, self.root, self.root / "auth")

    def test_a_different_token_is_not_set_up(self):
        game, skin = self.root / "game", self.root / "skin"
        game.mkdir()
        skin.mkdir()
        (skin / "skin_config.xml").write_text("<record/>")
        dll = self.root / "winmm.dll"
        dll.write_bytes(b"MZ dll")
        (game / "winmm.dll").write_bytes(b"MZ dll")
        digest = setup_fm24.native_install.digest(dll)
        (game / "fmface24-install.json").write_text('{"version": 2, "dllSha256": "%s", "panels": {}}' % digest)
        (game / "fmface24.ini").write_text(f"token={TOKEN}\n", encoding="ascii")
        auth = self.root / ".fm24-auth"
        auth.write_text(TOKEN + "\n", encoding="ascii")
        with mock.patch.object(setup_fm24, "bundled_dll", return_value=dll):
            self.assertTrue(setup_fm24.is_set_up(game, skin, auth))
            auth.write_text("u" * 43 + "\n", encoding="ascii")
            self.assertFalse(setup_fm24.is_set_up(game, skin, auth))
            auth.write_text(TOKEN + "\n", encoding="ascii")
            dll.write_bytes(b"MZ newer dll")
            self.assertFalse(setup_fm24.is_set_up(game, skin, auth))


if __name__ == "__main__":
    unittest.main()
