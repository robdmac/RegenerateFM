"""The macOS installer app (macos/installer), run with --cli against a stand-in
Steam folder and game folder: it sets launch options exactly as the desktop
program's SteamLaunch does, and installs and removes the FM26-macOS files."""

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
SOURCE = ROOT / "macos" / "installer" / "RegenerateFMInstaller.swift"
DESKTOP = ROOT / "src" / "RegenerateFM.Desktop"
LAUNCHER = '"/Users/you/Library/Application Support/Steam/steamapps/common/Football Manager 26/run_regeneratefm.sh"'

CONFIG = '''"UserLocalConfigStore"
{
\t"Software"
\t{
\t\t"Valve"
\t\t{
\t\t\t"Steam"
\t\t\t{
\t\t\t\t"apps"
\t\t\t\t{
\t\t\t\t\t"2252570"
\t\t\t\t\t{
\t\t\t\t\t\t"LastPlayed"\t\t"1777832597"
\t\t\t\t\t}
\t\t\t\t\t"3551340"
\t\t\t\t\t{
\t\t\t\t\t\t"LastPlayed"\t\t"1792255385"
\t\t\t\t\t\t"cloud"
\t\t\t\t\t\t{
\t\t\t\t\t\t\t"LaunchOptions"\t\t"not this one"
\t\t\t\t\t\t}
\t\t\t\t\t\t"Playtime"\t\t"3910"
\t\t\t\t\t}
\t\t\t\t}
\t\t\t}
\t\t}
\t}
\t"apps"
\t{
\t\t"3551340"\t\t"0123abcd"
\t}
}
'''


def _escape(value):
    return value.replace("\\", "\\\\").replace('"', '\\"')


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("swiftc") and shutil.which("dotnet"),
                     "the installer is built on a Mac, and checked against the desktop program")
class MacInstallerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = Path(tempfile.mkdtemp())
        cls.installer = cls.build / "installer"
        subprocess.run(["xcrun", "swiftc", "-parse-as-library", "-O", str(SOURCE), "-o", str(cls.installer)],
                       check=True, capture_output=True)
        subprocess.run(["dotnet", "build", str(DESKTOP), "-c", "Release", "-o", str(cls.build / "desktop"), "-v", "q"],
                       check=True, capture_output=True)
        cls.desktop = cls.build / "desktop" / "regeneratefm-fm24.dll"

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.build)

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)

    def expected(self, config, app, prefix, remove=False):
        """The desktop program's launch option: (output, error message)."""
        result = subprocess.run(["dotnet", str(self.desktop), "launch-option", str(config), app, prefix,
                                 *(["--remove"] if remove else [])], capture_output=True)
        return result.stdout, result.stderr.decode().strip() if result.returncode else None

    def cli(self, *args, steam=None, ok=True):
        """The installer's output, or its error message when ok is False."""
        env = dict(os.environ, REGENERATEFM_STEAM_ROOT=str(steam or self.tmp / "no-steam"))
        result = subprocess.run([str(self.installer), "--cli", *map(str, args)], capture_output=True, env=env)
        if not ok:
            self.assertEqual(result.returncode, 1)
            return result.stderr.decode().strip()
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        return result.stdout

    def test_launch_options_match_the_desktop_programs(self):
        set_already = CONFIG.replace('"Playtime"\t\t"3910"', f'"LaunchOptions"\t\t"{_escape(LAUNCHER)} %command% -x"')
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
                            output, error = self.expected(config, app, prefix, remove)
                            if error:
                                self.assertEqual(self.cli("launch-option", config, app, prefix, ok=False), error)
                                continue
                            self.assertEqual(self.cli("launch-option", config, app, prefix,
                                                      *(["--remove"] if remove else [])), output)

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
        # An account on this Mac that doesn't have FM26 is left alone.
        other = steam / "userdata" / "5678" / "config" / "localconfig.vdf"
        other.parent.mkdir(parents=True)
        other.write_text(CONFIG.replace('"3551340"', '"2252570"'))
        return steam, game.resolve(), config

    def make_payload(self):
        payload = self.tmp / "payload"
        for name, text in (("BepInEx/core/BepInEx.Unity.IL2CPP.dll", "core"),
                           ("BepInEx/plugins/RegenerateFM/RegenerateFM.dll", "plugin"),
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

        # An earlier build's plugin folder goes, so it never loads beside the new one.
        (game / "BepInEx" / "plugins" / "RegenerateFMHotReload").mkdir(parents=True)
        (game / "BepInEx" / "plugins" / "RegenerateFMHotReload" / "RegenerateFMHotReload.dll").write_text("old")
        self.assertEqual(self.cli("needs-change", game, steam=steam), b"true\n")
        output = self.cli("install", payload, game, steam=steam).decode()
        self.assertIn("copied 6", output)
        self.assertIn("launch options changed 1", output)
        self.assertEqual((game / "BepInEx/plugins/RegenerateFM/RegenerateFM.dll").read_text(), "plugin")
        self.assertFalse((game / "BepInEx/plugins/RegenerateFMHotReload").exists())
        self.assertTrue(os.access(game / "run_regeneratefm.sh", os.X_OK))
        self.assertEqual(os.readlink(game / "Data"), "fm.app/Contents/Resources/Data")
        attributes = subprocess.run(["xattr", str(game / "libdoorstop.dylib")], capture_output=True, text=True).stdout
        self.assertNotIn("com.apple.quarantine", attributes)
        prefix = f'"{game}/run_regeneratefm.sh"'
        original = self.tmp / "original.vdf"
        original.write_text(CONFIG)
        self.assertEqual(config.read_bytes(), self.expected(original, "3551340", prefix)[0])
        self.assertEqual(Path(str(config) + ".regeneratefm-backup").read_text(), CONFIG)
        self.assertEqual(self.cli("needs-change", game, steam=steam), b"false\n")
        self.assertEqual(self.cli("needs-change", game, "--remove", steam=steam), b"true\n")
        # Again: nothing more to change.
        self.assertIn("launch options changed 0", self.cli("install", payload, game, steam=steam).decode())

        # Another mod's plugin keeps BepInEx; only RegenerateFM's goes.
        (game / "BepInEx" / "plugins" / "Other").mkdir()
        self.assertIn("other plugins: Other", self.cli("uninstall", game, steam=steam).decode())
        self.assertFalse((game / "BepInEx/plugins/RegenerateFM").exists())
        self.assertTrue((game / "dotnet").exists())

        shutil.rmtree(game / "BepInEx" / "plugins" / "Other")
        self.cli("install", payload, game, steam=steam)
        self.assertIn("Removed RegenerateFM", self.cli("uninstall", game, steam=steam).decode())
        self.assertEqual(sorted(path.name for path in game.iterdir()), ["fm.app"])
        other = steam / "userdata" / "5678" / "config" / "localconfig.vdf"
        self.assertEqual(other.read_text(), CONFIG.replace('"3551340"', '"2252570"'))
        self.assertFalse(Path(str(other) + ".regeneratefm-backup").exists())
        self.assertIn('"LaunchOptions"\t\t""', config.read_text())


if __name__ == "__main__":
    unittest.main()
