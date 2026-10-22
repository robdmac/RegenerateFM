"""The macOS installer app (hotreload/macos/installer), run with --cli against
a stand-in Steam folder and game folder: it sets launch options exactly as
steam_launch.py does, and installs and removes the FM26-macOS files."""

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import steam_launch  # noqa: E402
from test_steam_launch import CONFIG, LAUNCHER  # noqa: E402

SOURCE = ROOT / "hotreload" / "macos" / "installer" / "RegenerateFMInstaller.swift"


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("swiftc"), "the installer is built on a Mac")
class MacInstallerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = Path(tempfile.mkdtemp())
        cls.installer = cls.build / "installer"
        subprocess.run(["xcrun", "swiftc", "-parse-as-library", "-O", str(SOURCE), "-o", str(cls.installer)],
                       check=True, capture_output=True)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.build)

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)

    def cli(self, *args, steam=None, ok=True):
        """The installer's output, or its error message when ok is False."""
        env = dict(os.environ, REGENERATEFM_STEAM_ROOT=str(steam or self.tmp / "no-steam"))
        result = subprocess.run([str(self.installer), "--cli", *map(str, args)], capture_output=True, env=env)
        if not ok:
            self.assertEqual(result.returncode, 1)
            return result.stderr.decode().strip()
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        return result.stdout

    def test_launch_options_match_steam_launch(self):
        set_already = CONFIG.replace('"Playtime"\t\t"3910"', f'"LaunchOptions"\t\t"{steam_launch._escape(LAUNCHER)} %command% -x"')
        own_option = CONFIG.replace('"Playtime"\t\t"3910"', '"LaunchOptions"\t\t"MANGOHUD=1 %command% -windowed"')
        unicode = "// comment ü\n" + CONFIG.replace('"1792255385"', '"Zoë \\"quoted\\""')
        prefixes = (LAUNCHER, '"/Users/zoë/Football Manager 26/run_regeneratefm.sh"')
        broken = CONFIG.replace('"apps"\n\t{', '\n\t{', 1)
        for text in (CONFIG, set_already, own_option, unicode, broken):
            for app in ("3551340", "2252570", "999"):
                for prefix in prefixes:
                    for remove in (False, True):
                        with self.subTest(app=app, prefix=prefix, remove=remove, text=text[:40]):
                            config = self.tmp / "localconfig.vdf"
                            config.write_bytes(text.encode())
                            try:
                                expected = steam_launch.set_launch_option(text, app, prefix, remove=remove)
                            except ValueError as exc:
                                self.assertEqual(self.cli("launch-option", config, app, prefix, ok=False), str(exc))
                                continue
                            output = self.cli("launch-option", config, app, prefix, *(["--remove"] if remove else []))
                            self.assertEqual(output, b"NONE\n" if expected is None else expected.encode())

    def make_steam(self):
        steam = self.tmp / "Steam"
        library = self.tmp / "Other Library"
        (steam / "steamapps").mkdir(parents=True)
        (library / "steamapps").mkdir(parents=True)
        (steam / "steamapps" / "libraryfolders.vdf").write_text(
            f'"libraryfolders"\n{{\n\t"0"\n\t{{\n\t\t"path"\t\t"{steam}"\n\t}}\n'
            f'\t"1"\n\t{{\n\t\t"path"\t\t"{library}"\n\t}}\n}}\n')
        game = library / "steamapps" / "common" / "Football Manager 26"
        (game / "fm.app" / "Contents").mkdir(parents=True)
        config = steam / "userdata" / "1234" / "config" / "localconfig.vdf"
        config.parent.mkdir(parents=True)
        config.write_text(CONFIG)
        return steam, game.resolve(), config

    def make_payload(self):
        payload = self.tmp / "payload"
        for name, text in (("BepInEx/core/BepInEx.Unity.IL2CPP.dll", "core"),
                           ("BepInEx/plugins/RegenerateFMHotReload/RegenerateFMHotReload.dll", "plugin"),
                           ("dotnet/libcoreclr.dylib", "clr"), ("libdoorstop.dylib", "doorstop"),
                           (".doorstop_version", "4.3"), ("run_regeneratefm.sh", "#!/bin/sh\n")):
            path = payload / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text)
        (payload / "run_regeneratefm.sh").chmod(0o755)
        subprocess.run(["xattr", "-w", "com.apple.quarantine", "0081;00000000;Safari;", str(payload / "libdoorstop.dylib")],
                       check=True)
        return payload

    def test_install_update_and_uninstall(self):
        steam, game, config = self.make_steam()
        payload = self.make_payload()
        self.assertEqual(self.cli("games", steam=steam).decode(), f"3551340 {game}\n")

        (game / "BepInEx" / "plugins" / "RegenerateFMHotReload").mkdir(parents=True)
        (game / "BepInEx" / "plugins" / "RegenerateFMHotReload" / "RegenerateFMHotReload.dll").write_text("old")
        output = self.cli("install", payload, game, steam=steam).decode()
        self.assertIn("copied 6", output)
        self.assertIn("launch options changed 1", output)
        self.assertEqual((game / "BepInEx/plugins/RegenerateFMHotReload/RegenerateFMHotReload.dll").read_text(), "plugin")
        self.assertTrue(os.access(game / "run_regeneratefm.sh", os.X_OK))
        self.assertEqual(os.readlink(game / "Data"), "fm.app/Contents/Resources/Data")
        attributes = subprocess.run(["xattr", str(game / "libdoorstop.dylib")], capture_output=True, text=True).stdout
        self.assertNotIn("com.apple.quarantine", attributes)
        prefix = f'"{game}/run_regeneratefm.sh"'
        self.assertEqual(config.read_text(), steam_launch.set_launch_option(CONFIG, "3551340", prefix))
        self.assertEqual(Path(str(config) + ".regeneratefm-backup").read_text(), CONFIG)
        # Again: nothing more to change.
        self.assertIn("launch options changed 0", self.cli("install", payload, game, steam=steam).decode())

        # Another mod's plugin keeps BepInEx; only RegenerateFM's goes.
        (game / "BepInEx" / "plugins" / "Other").mkdir()
        self.assertIn("other plugins: Other", self.cli("uninstall", game, steam=steam).decode())
        self.assertFalse((game / "BepInEx/plugins/RegenerateFMHotReload").exists())
        self.assertTrue((game / "dotnet").exists())

        shutil.rmtree(game / "BepInEx" / "plugins" / "Other")
        self.cli("install", payload, game, steam=steam)
        self.assertIn("Removed RegenerateFM", self.cli("uninstall", game, steam=steam).decode())
        self.assertEqual(sorted(path.name for path in game.iterdir()), ["fm.app"])
        installed = steam_launch.set_launch_option(CONFIG, "3551340", prefix)
        self.assertEqual(config.read_text(), steam_launch.set_launch_option(installed, "3551340", prefix, remove=True))
        self.assertIn('"LaunchOptions"\t\t""', config.read_text())


if __name__ == "__main__":
    unittest.main()
