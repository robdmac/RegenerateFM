"""install_linux.py, against a stand-in Steam folder, release and game folders:
it installs and removes FM26's files and launch option, keeps other mods'
plugins, and checks Steam and the games first."""

import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import steam_launch  # noqa: E402
from test_steam_launch import CONFIG  # noqa: E402

if sys.platform.startswith("linux"):
    import install_linux  # noqa: E402

FM26_FILES = {"winhttp.dll": b"MZ winhttp", "doorstop_config.ini": b"[General]\n", ".doorstop_version": b"4",
              "dotnet/coreclr.dll": b"MZ coreclr", "BepInEx/core/BepInEx.Unity.IL2CPP.dll": b"MZ bepinex",
              "BepInEx/plugins/RegenerateFMHotReload/RegenerateFMHotReload.dll": b"MZ plugin"}


@unittest.skipUnless(sys.platform.startswith("linux"), "the Linux installer")
class LinuxInstallerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.steam = self.tmp / "steam"
        self.config = self.steam / "userdata" / "1" / "config" / "localconfig.vdf"
        self.config.parent.mkdir(parents=True)
        self.config.write_text(CONFIG)
        common = self.steam / "steamapps" / "common"
        self.fm26, self.fm24 = common / "Football Manager 26", common / "Football Manager 2024"
        for game in (self.fm26, self.fm24):
            game.mkdir(parents=True)
            (game / "fm.exe").write_bytes(b"MZ fm")
        self.release = self.tmp / "release"
        for name, data in FM26_FILES.items():
            path = self.release / "FM26" / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        for name in ("regeneratefm-fm24.exe", "_internal/python311.dll", "install-linux.sh"):
            path = self.release / "FM24" / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"MZ")
        self.data = self.tmp / "data"
        self.running = []
        self.steam_running = False
        for target, name, value in (
                (steam_launch, "steam_roots", lambda: [self.steam]),
                (steam_launch, "steam_running", lambda: self.steam_running),
                (install_linux, "running_executables", lambda: iter(self.running))):
            patcher = mock.patch.object(target, name, side_effect=value)
            patcher.start()
            self.addCleanup(patcher.stop)
        environment = mock.patch.dict(os.environ, {"XDG_DATA_HOME": str(self.data)})
        environment.start()
        self.addCleanup(environment.stop)
        self.setup_calls = []
        patcher = mock.patch.object(install_linux, "run_fm24_setup", side_effect=self.setup_calls.append)
        patcher.start()
        self.addCleanup(patcher.stop)

    def run_installer(self, *args):
        with mock.patch("builtins.print"):
            return install_linux.main(["--release", str(self.release), *map(str, args)])

    def files(self, folder):
        return {str(path.relative_to(folder)): path.read_bytes() for path in folder.rglob("*") if path.is_file()}

    def launch_option(self, app):
        return steam_launch._app_block(self.config.read_text(), app)[2]

    def test_fm26_install_and_remove(self):
        (self.fm26 / "winhttp.dll").write_bytes(b"MZ older")
        self.assertEqual(self.run_installer("fm26"), 0)
        installed = self.files(self.fm26)
        for name, data in FM26_FILES.items():
            self.assertEqual(installed[name], data)
        self.assertEqual(self.launch_option("3551340")[2], 'WINEDLLOVERRIDES="winhttp=n,b" %command%')
        self.assertIsNone(self.launch_option("2252570"))
        self.assertEqual(self.setup_calls, [])

        self.assertEqual(self.run_installer("fm26", "--remove"), 0)
        self.assertEqual(self.files(self.fm26), {"fm.exe": b"MZ fm"})
        self.assertEqual(self.launch_option("3551340")[2], "")

    def test_another_mods_plugin_keeps_bepinex(self):
        self.run_installer("fm26")
        other = self.fm26 / "BepInEx" / "plugins" / "OtherMod.dll"
        other.write_bytes(b"MZ other")
        self.run_installer("fm26", "--remove")
        self.assertTrue(other.is_file())
        self.assertTrue((self.fm26 / "winhttp.dll").is_file())
        self.assertFalse((self.fm26 / install_linux.PLUGIN).exists())

    def test_nothing_changes_while_steam_or_the_game_runs(self):
        self.steam_running = True
        with self.assertRaises(SystemExit) as raised:
            self.run_installer()
        self.assertIn("Quit Steam", str(raised.exception))
        self.steam_running = False
        self.running = [(7, r"Z:\steam\steamapps\common\Football Manager 26\fm.exe")]
        with self.assertRaises(SystemExit) as raised:
            self.run_installer()
        self.assertIn("Close Football Manager 26", str(raised.exception))
        self.assertEqual(self.files(self.fm26), {"fm.exe": b"MZ fm"})
        self.assertEqual(self.config.read_text(), CONFIG)
        self.assertEqual(self.setup_calls, [])

    def test_steam_may_run_when_the_launch_options_are_set(self):
        self.run_installer("fm26")
        self.steam_running = True
        self.assertEqual(self.run_installer("fm26"), 0)

    def test_every_game_found_and_the_fm24_companion(self):
        companion = self.data / "RegenerateFM" / "FM24"
        self.assertEqual(self.run_installer(), 0)
        self.assertTrue((self.fm26 / "winhttp.dll").is_file())
        self.assertEqual(sorted(self.files(companion)), ["_internal/python311.dll", "regeneratefm-fm24.exe"])
        self.assertEqual(self.setup_calls, [["--game-dir", str(self.fm24), "--companion",
                                             str(companion / "regeneratefm-fm24.exe")]])

        (self.fm24 / "regeneratefm24-install.json").write_text("{}")
        (companion / ".fm24-auth").write_text("token")
        (companion / ".env").write_text("OPENAI_API_KEY=")
        self.assertEqual(self.run_installer("--remove"), 0)
        self.assertEqual(self.setup_calls[-1], ["--game-dir", str(self.fm24), "--remove"])
        self.assertEqual(sorted(self.files(companion)), [".env"])

    def test_a_failed_game_does_not_stop_the_others(self):
        self.setup_calls = None
        with mock.patch.object(install_linux, "run_fm24_setup",
                               side_effect=install_linux.InstallError("FM24 isn't 24.4.2")), \
                mock.patch("sys.stderr"):
            self.assertEqual(self.run_installer(), 1)
        self.assertTrue((self.fm26 / "winhttp.dll").is_file())

    def test_a_named_game_must_be_found(self):
        shutil.rmtree(self.fm26)
        with mock.patch("sys.stderr"), self.assertRaises(SystemExit):
            self.run_installer("fm26")
        with mock.patch("sys.stderr"), self.assertRaises(SystemExit):
            self.run_installer("fm26", "fm24", "--game-dir", self.fm24)


if __name__ == "__main__":
    unittest.main()
