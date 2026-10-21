import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import steam_launch  # noqa: E402

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


class LaunchOptionTests(unittest.TestCase):
    def test_regeneratefm_goes_in_front_of_the_players_own_option(self):
        self.assertEqual(steam_launch.combined("", LAUNCHER), f"{LAUNCHER} %command%")
        self.assertEqual(steam_launch.combined("-windowed", LAUNCHER), f"{LAUNCHER} %command% -windowed")
        self.assertEqual(steam_launch.combined("MANGOHUD=1 %command% -x", 'WINEDLLOVERRIDES="winhttp=n,b"'),
                         'MANGOHUD=1 WINEDLLOVERRIDES="winhttp=n,b" %command% -x')
        already = f"{LAUNCHER} %command%"
        self.assertEqual(steam_launch.combined(already, LAUNCHER), already)
        self.assertEqual(steam_launch.without(already, LAUNCHER), "")
        self.assertEqual(steam_launch.without(f"{LAUNCHER} %command% -windowed", LAUNCHER), "%command% -windowed")

    def test_the_option_is_added_to_the_games_own_block_only(self):
        updated = steam_launch.set_launch_option(CONFIG, "3551340", LAUNCHER)
        expected = '\t\t\t\t\t\t"Playtime"\t\t"3910"\n\t\t\t\t\t\t"LaunchOptions"\t\t"' + \
                   steam_launch._escape(f"{LAUNCHER} %command%") + '"\n\t\t\t\t\t}'
        self.assertIn(expected, updated)
        # Nothing else changes: the nested block's key and the other app are untouched.
        self.assertEqual(updated.replace(expected, '\t\t\t\t\t\t"Playtime"\t\t"3910"\n\t\t\t\t\t}'), CONFIG)
        self.assertEqual(steam_launch._app_block(updated, "3551340")[2][2], f"{LAUNCHER} %command%")
        # Setting it again changes nothing; removing it leaves an empty option.
        self.assertEqual(steam_launch.set_launch_option(updated, "3551340", LAUNCHER), updated)
        removed = steam_launch.set_launch_option(updated, "3551340", LAUNCHER, remove=True)
        self.assertEqual(steam_launch._app_block(removed, "3551340")[2][2], "")

    def test_an_account_without_the_game_is_left_alone(self):
        self.assertIsNone(steam_launch.set_launch_option(CONFIG, "1234", LAUNCHER))

    def test_accounts_are_changed_with_a_backup_and_never_while_steam_runs(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            owner = root / "userdata" / "1" / "config" / "localconfig.vdf"
            other = root / "userdata" / "2" / "config" / "localconfig.vdf"
            for path, text in ((owner, CONFIG), (other, '"UserLocalConfigStore"\n{\n}\n')):
                path.parent.mkdir(parents=True)
                path.write_text(text)
            self.assertEqual(steam_launch.update_accounts("3551340", LAUNCHER, roots=[root]), [owner])
            self.assertIn("run_regeneratefm.sh", owner.read_text())
            self.assertEqual((owner.parent / "localconfig.vdf.regeneratefm-backup").read_text(), CONFIG)
            self.assertEqual(other.read_text(), '"UserLocalConfigStore"\n{\n}\n')
            game = Path("/Games/Football Manager 26")
            with mock.patch.object(steam_launch, "steam_running", return_value=True), \
                    mock.patch.object(steam_launch, "needs_change", return_value=True), \
                    mock.patch.object(steam_launch, "update_accounts") as update, \
                    mock.patch.object(steam_launch, "regeneratefm_option",
                                      return_value=f'"{game / "run_regeneratefm.sh"}"'):
                self.assertIn("Quit Steam", steam_launch.apply("fm26", game))
            update.assert_not_called()

    @unittest.skipIf(sys.platform == "win32", "Windows needs no launch option")
    def test_an_option_already_set_needs_no_change_even_with_steam_running(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            config = root / "userdata" / "1" / "config" / "localconfig.vdf"
            config.parent.mkdir(parents=True)
            config.write_text(CONFIG)
            game = Path("/Games/Football Manager 26")
            prefix = steam_launch.regeneratefm_option("fm26", game)
            self.assertTrue(steam_launch.needs_change("fm26", game, roots=[root]))
            steam_launch.update_accounts("3551340", prefix, roots=[root])
            self.assertFalse(steam_launch.needs_change("fm26", game, roots=[root]))
            with mock.patch.object(steam_launch, "steam_roots", return_value=[root]), \
                    mock.patch.object(steam_launch, "steam_running", return_value=True):
                self.assertIn("already set", steam_launch.apply("fm26", game))

    def test_the_fm26_demo_is_its_own_app_with_fm26s_launcher(self):
        self.assertEqual(steam_launch.GAMES["fm26demo"]["app"], "3551360")
        demo = Path("/Games/Football Manager 26 Demo")
        self.assertEqual(steam_launch.regeneratefm_option("fm26demo", demo, "darwin"), f'"{demo / "run_regeneratefm.sh"}"')
        self.assertEqual(steam_launch.regeneratefm_option("fm26demo", demo, "linux"), 'WINEDLLOVERRIDES="winhttp=n,b"')

    def test_options_per_platform(self):
        game = Path("/Games/Football Manager 2024")
        self.assertEqual(steam_launch.regeneratefm_option("fm24", game, "darwin"), f'"{game / "run_regeneratefm24.sh"}"')
        self.assertEqual(steam_launch.regeneratefm_option("fm24", game, "linux"), 'WINEDLLOVERRIDES="winmm=n,b"')
        self.assertIsNone(steam_launch.regeneratefm_option("fm26", game, "win32"))


if __name__ == "__main__":
    unittest.main()
