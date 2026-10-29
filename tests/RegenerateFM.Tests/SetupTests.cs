using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RegenerateFM.Desktop;
using RegenerateFM.Desktop.Epic;
using RegenerateFM.Desktop.FM24;
using RegenerateFM.Desktop.FM24.Setup;
using RegenerateFM.Desktop.Linux;
using RegenerateFM.Desktop.Steam;
using Xunit;

namespace RegenerateFM.Tests
{
    // Steam's launch options, what Epic starts, FM24's setup and DLL install,
    // and the Linux installer, against stand-in Steam and Epic folders, games
    // and releases.
    public class SetupTests : IDisposable
    {
        public const string Token = "kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk";
        private const string PlayerPanel = "<panel>\n\t<container>\n\t\t<widget class=\"player_personal_details_panel\" id=\"ppdp\" height=\"80\" />\n\t</container>\n</panel>\n";
        private const string StaffPanel = "<panel>\n\t<container>\n\t\t<widget class=\"picture\" id=\"ppic\" />\n\t</container>\n</panel>\n";
        private const string SquadPanel = "<panel>\n\t<container>\n\t\t<widget class=\"popup_button\" id=\"apic\" />\n\t\t<!-- quick pick -->\n\t</container>\n</panel>\n";
        private const string Launcher = "\"/Users/you/Library/Application Support/Steam/steamapps/common/Football Manager 26/run_regeneratefm.sh\"";
        public const string Config =
            "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
            "\t\t\t\t\t\"2252570\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1777832597\"\n\t\t\t\t\t}\n" +
            "\t\t\t\t\t\"3551340\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1792255385\"\n\t\t\t\t\t\t\"cloud\"\n\t\t\t\t\t\t{\n" +
            "\t\t\t\t\t\t\t\"LaunchOptions\"\t\t\"not this one\"\n\t\t\t\t\t\t}\n\t\t\t\t\t\t\"Playtime\"\t\t\"3910\"\n\t\t\t\t\t}\n" +
            "\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n\t\"apps\"\n\t{\n\t\t\"3551340\"\t\t\"0123abcd\"\n\t}\n}\n";

        private readonly TempFolder _root = new TempFolder();
        private readonly string _steam, _epic;
        private bool _steamRunning, _epicRunning;

        public SetupTests()
        {
            _steam = _root.Combine("steam");
            Directory.CreateDirectory(Path.Combine(_steam, "steamapps"));
            SteamLaunch.Roots = () => new List<string> { _steam };
            SteamLaunch.Running = () => _steamRunning;
            _epic = _root.Combine("epic-manifests");
            EpicLaunch.ManifestsDir = () => _epic;
            EpicLaunch.Running = () => _epicRunning;
        }

        public void Dispose()
        {
            SteamLaunch.Roots = SteamLaunch.DefaultRoots;
            SteamLaunch.Running = SteamLaunch.SteamRunning;
            EpicLaunch.ManifestsDir = EpicLaunch.DefaultManifestsDir;
            EpicLaunch.Running = EpicLaunch.EpicRunning;
            _root.Dispose();
        }

        // ---- What Epic starts ------------------------------------------------------

        // Epic's record of a game installed in ``folder``, as the Mac launcher writes it.
        private string EpicRecord(string folder, string program = Fm24Setup.MacProgram)
        {
            Directory.CreateDirectory(_epic);
            string record = Path.Combine(_epic, "C58BCA963D445FD06E0DFC85EAF0C003.item");
            File.WriteAllText(record, new JsonObject
            {
                ["FormatVersion"] = 0, ["LaunchCommand"] = "", ["LaunchExecutable"] = program,
                ["InstallLocation"] = folder, ["AppName"] = "da2038ef19e94554898056d79168b3f8", ["bRequiresAuth"] = true,
            }.ToJsonString());
            return record;
        }

        private static string EpicProgram(string record) => (string)JsonNode.Parse(File.ReadAllText(record))!["LaunchExecutable"];

        [Fact]
        public void Fm24IsFoundWhereEpicInstalledItWhenSteamHasNone()
        {
            string game = _root.Combine("Epic Games", Fm24Setup.EpicFolder);
            string executable = OperatingSystem.IsMacOS() ? Fm24Setup.MacProgram : "fm.exe";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(game, executable)));
            File.WriteAllText(Path.Combine(game, executable), "");
            EpicRecord(game + "/");
            Assert.Equal(game, Fm24Setup.FindGameDir());
            // Steam's copy comes first.
            string steam = Path.Combine(_steam, "steamapps", "common", Fm24Setup.GameFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(steam, executable)));
            File.WriteAllText(Path.Combine(steam, executable), "");
            Assert.Equal(Path.Combine(SteamLaunch.Resolve(_steam), "steamapps", "common", Fm24Setup.GameFolder), Fm24Setup.FindGameDir());
        }

        [Fact]
        public void EpicStartsTheLauncherOnlyWhileItIsClosedAndCanStartTheGameAgain()
        {
            string game = _root.Combine("Epic Games", Fm24Setup.EpicFolder);
            string record = EpicRecord(game);
            string original = File.ReadAllText(record);
            Assert.True(EpicLaunch.NeedsChange(game, Fm24Setup.MacLauncher, Fm24Setup.MacProgram));
            _epicRunning = true;
            Assert.Contains("is running", EpicLaunch.Apply(game, Fm24Setup.MacLauncher, Fm24Setup.MacProgram));
            Assert.Equal(original, File.ReadAllText(record));
            _epicRunning = false;
            Assert.StartsWith("Epic now starts the game with RegenerateFM", EpicLaunch.Apply(game, Fm24Setup.MacLauncher, Fm24Setup.MacProgram));
            Assert.Equal(Fm24Setup.MacLauncher, EpicProgram(record));
            var item = JsonNode.Parse(File.ReadAllText(record))!;
            Assert.Equal(game, (string)item["InstallLocation"]);
            Assert.True((bool)item["bRequiresAuth"]);
            Assert.Equal(original, File.ReadAllText(record + EpicLaunch.BackupSuffix));
            Assert.False(EpicLaunch.NeedsChange(game, Fm24Setup.MacLauncher, Fm24Setup.MacProgram));
            Assert.Contains("already starts", EpicLaunch.Apply(game, Fm24Setup.MacLauncher, Fm24Setup.MacProgram));
            Assert.StartsWith("Epic starts the game itself again", EpicLaunch.Apply(game, Fm24Setup.MacLauncher, Fm24Setup.MacProgram, remove: true));
            Assert.Equal(Fm24Setup.MacProgram, EpicProgram(record));
            // A game Epic didn't install has no Epic setting.
            Assert.Null(EpicLaunch.Record(_root.Combine("Football Manager 2024")));
            Assert.Contains("didn't install", EpicLaunch.Apply(_root.Combine("Football Manager 2024"), Fm24Setup.MacLauncher, Fm24Setup.MacProgram));
        }

        // ---- Steam's launch options ----------------------------------------------

        [Fact]
        public void RegenerateFMGoesInFrontOfThePlayersOwnOption()
        {
            Assert.Equal(Launcher + " %command%", SteamLaunch.Combined("", Launcher));
            Assert.Equal(Launcher + " %command% -windowed", SteamLaunch.Combined("-windowed", Launcher));
            Assert.Equal("MANGOHUD=1 WINEDLLOVERRIDES=\"winhttp=n,b\" %command% -x", SteamLaunch.Combined("MANGOHUD=1 %command% -x", "WINEDLLOVERRIDES=\"winhttp=n,b\""));
            string already = Launcher + " %command%";
            Assert.Equal(already, SteamLaunch.Combined(already, Launcher));
            Assert.Equal("", SteamLaunch.Without(already, Launcher));
            Assert.Equal("%command% -windowed", SteamLaunch.Without(Launcher + " %command% -windowed", Launcher));
        }

        [Fact]
        public void TheOptionIsAddedToTheGamesOwnBlockOnly()
        {
            string updated = SteamLaunch.SetLaunchOption(Config, "3551340", Launcher);
            string expected = "\t\t\t\t\t\t\"Playtime\"\t\t\"3910\"\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"" +
                              (Launcher + " %command%").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"\n\t\t\t\t\t}";
            Assert.Contains(expected, updated);
            // Nothing else changes: the nested block's key and the other app are untouched.
            Assert.Equal(Config, updated.Replace(expected, "\t\t\t\t\t\t\"Playtime\"\t\t\"3910\"\n\t\t\t\t\t}"));
            Assert.Equal(Launcher + " %command%", SteamLaunch.AppBlock(updated, "3551340").Value.Value.Value.Value);
            // Setting it again changes nothing; removing it leaves an empty option.
            Assert.Equal(updated, SteamLaunch.SetLaunchOption(updated, "3551340", Launcher));
            Assert.Equal("", SteamLaunch.AppBlock(SteamLaunch.SetLaunchOption(updated, "3551340", Launcher, remove: true), "3551340").Value.Value.Value.Value);
            Assert.Null(SteamLaunch.SetLaunchOption(Config, "1234", Launcher));
        }

        private string Account(string id, string text)
        {
            string config = Path.Combine(_steam, "userdata", id, "config", "localconfig.vdf");
            Directory.CreateDirectory(Path.GetDirectoryName(config));
            File.WriteAllText(config, text);
            return config;
        }

        [NotOnWindowsFact]
        public void AccountsAreChangedWithABackupAndNeverWhileSteamRuns()
        {
            string owner = Account("1", Config), other = Account("2", "\"UserLocalConfigStore\"\n{\n}\n");
            Assert.Equal(new[] { owner }, SteamLaunch.UpdateAccounts("3551340", Launcher));
            Assert.Contains("run_regeneratefm.sh", File.ReadAllText(owner));
            Assert.Equal(Config, File.ReadAllText(owner + SteamLaunch.BackupSuffix));
            Assert.Equal("\"UserLocalConfigStore\"\n{\n}\n", File.ReadAllText(other));
            File.WriteAllText(owner, Config);
            _steamRunning = true;
            Assert.Contains("Quit Steam", SteamLaunch.Apply("fm26", "/Games/Football Manager 26"));
            Assert.Equal(Config, File.ReadAllText(owner));
            _steamRunning = false;
            Assert.True(SteamLaunch.NeedsChange("fm26", "/Games/Football Manager 26"));
            Assert.StartsWith("Set the Steam launch option", SteamLaunch.Apply("fm26", "/Games/Football Manager 26"));
            Assert.False(SteamLaunch.NeedsChange("fm26", "/Games/Football Manager 26"));
            // An option already set needs no change, even with Steam running.
            _steamRunning = true;
            Assert.Contains("already set", SteamLaunch.Apply("fm26", "/Games/Football Manager 26"));
        }

        [Fact]
        public void OptionsPerGameAndPlatform()
        {
            Assert.Equal("3551360", SteamLaunch.Games["fm26demo"].App);
            string demo = "/Games/Football Manager 26 Demo", fm24 = "/Games/Football Manager 2024";
            Assert.Equal("\"" + Path.Combine(demo, "run_regeneratefm.sh") + "\"", SteamLaunch.Option("fm26demo", demo, "macos"));
            Assert.Equal("WINEDLLOVERRIDES=\"winhttp=n,b\"", SteamLaunch.Option("fm26demo", demo, "linux"));
            Assert.Equal("\"" + Path.Combine(fm24, "run_regeneratefm24.sh") + "\"", SteamLaunch.Option("fm24", fm24, "macos"));
            Assert.Equal("WINEDLLOVERRIDES=\"winmm=n,b\"", SteamLaunch.Option("fm24", fm24, "linux"));
            Assert.Null(SteamLaunch.Option("fm26", fm24, "windows"));
        }

        private string Library(string name, string folder = "Football Manager 2024")
        {
            string game = Path.Combine(_root.Combine(name), "steamapps", "common", folder);
            Directory.CreateDirectory(game);
            File.WriteAllBytes(Path.Combine(game, "fm.exe"), Encoding.ASCII.GetBytes("MZ fm"));
            return _root.Combine(name);
        }

        [Fact]
        public void Fm24IsFoundInAnySteamLibraryAndItsUserDataInTheProtonPrefix()
        {
            string other = Library("games");
            File.WriteAllText(Path.Combine(_steam, "steamapps", "libraryfolders.vdf"),
                              "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + _steam + "\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + other + "\"\n\t}\n}\n");
            Assert.Equal(new[] { SteamLaunch.Resolve(_steam), SteamLaunch.Resolve(other) }, SteamLaunch.Libraries());
            string game = Path.Combine(SteamLaunch.Resolve(other), "steamapps", "common", "Football Manager 2024");
            Assert.Equal(game, SteamLaunch.FindGameDir("fm24", "fm.exe"));
            var setup = new Fm24Setup(new NativeInstall(mac: false)) { Proton = true };
            // The Proton prefix is Linux's; Windows and the Mac read the player's own folders.
            bool linux = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
            if (linux)
                Assert.Null(setup.FindUserDataDir(game));
            string userData = Path.Combine(SteamLaunch.Resolve(other), "steamapps", "compatdata", "2252570", "pfx", "drive_c", "users", "steamuser",
                                           "Documents", "Sports Interactive", "Football Manager 2024");
            Directory.CreateDirectory(userData);
            if (linux)
                Assert.Equal(userData, setup.FindUserDataDir(game));
        }

        // ---- FM24's native library -----------------------------------------------

        private sealed class Fm24
        {
            public readonly string Game, Skin, Dll;
            public readonly NativeInstall Native;
            public readonly byte[] Library = Encoding.ASCII.GetBytes("MZ...REGENERATEFM24_NATIVE_MENU_HOOK_INSTALLED...");
            public readonly List<string> Logged = new List<string>();

            public Fm24(TempFolder root)
            {
                Game = root.Combine("game");
                Skin = root.Combine("skin");
                Directory.CreateDirectory(Game);
                File.WriteAllBytes(Path.Combine(Game, "fm.exe"), Encoding.ASCII.GetBytes("fm24"));
                foreach (var (kind, text) in new[] { ("player", PlayerPanel), ("staff", StaffPanel) })
                {
                    string panel = Path.Combine(Skin, NativeInstall.PanelFiles[kind]);
                    Directory.CreateDirectory(Path.GetDirectoryName(panel));
                    File.WriteAllText(panel, text);
                }
                Dll = Path.Combine(Game, "winmm.dll");
                Native = new NativeInstall(mac: false)
                {
                    GameRunning = _ => false, SupportedSha256 = NativeInstall.Digest(Path.Combine(Game, "fm.exe")), Log = Logged.Add,
                };
            }

            public void Install(string companion = null) => Native.Install(Game, Skin, Library, Token, companion);
            public void Remove() => Native.Remove(Game, Skin);

            public Dictionary<string, byte[]> Snapshot() =>
                new[] { Game, Skin }.SelectMany(folder => Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                                    .ToDictionary(path => path, File.ReadAllBytes);
        }

        private static void AssertSame(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
        {
            Assert.Equal(expected.Keys.OrderBy(key => key), actual.Keys.OrderBy(key => key));
            foreach (var (path, data) in expected)
                Assert.Equal(data, actual[path]);
        }

        [Fact]
        public void InstallThenRemoveRestoresEverything()
        {
            var fm24 = new Fm24(_root);
            var before = fm24.Snapshot();
            fm24.Install();
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(fm24.Game, NativeInstall.Manifest)));
            Assert.Equal(new[] { NativeInstall.PanelFiles["player"], NativeInstall.PanelFiles["staff"] }.OrderBy(name => name, StringComparer.Ordinal),
                         manifest["panels"].AsObject().Select(entry => entry.Key).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Contains("FMFU", File.ReadAllText(Path.Combine(fm24.Skin, NativeInstall.PanelFiles["player"])));
            fm24.Remove();
            AssertSame(before, fm24.Snapshot());
        }

        [Fact]
        public void RemovingOneOfTwoFm24sKeepsTheSkinTheyShare()
        {
            // Steam's and Epic's FM24 share the user-data folder and so the skin.
            string executable = OperatingSystem.IsMacOS() ? Fm24Setup.MacProgram : "fm.exe";
            string steam = Path.Combine(_steam, "steamapps", "common", Fm24Setup.GameFolder);
            string epic = _root.Combine("Epic Games", Fm24Setup.EpicFolder);
            foreach (string game in new[] { steam, epic })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(game, executable)));
                File.WriteAllText(Path.Combine(game, executable), "");
            }
            EpicRecord(epic);
            Assert.False(Fm24Setup.OtherCopySetUp(epic));
            File.WriteAllText(Path.Combine(steam, NativeInstall.Manifest), "{}");
            Assert.True(Fm24Setup.OtherCopySetUp(epic));
            Assert.False(Fm24Setup.OtherCopySetUp(steam));

            var fm24 = new Fm24(_root);
            fm24.Install();
            string panel = Path.Combine(fm24.Skin, NativeInstall.PanelFiles["player"]);
            string installed = File.ReadAllText(panel);
            fm24.Native.Remove(fm24.Game, fm24.Skin, keepPanels: true);
            Assert.Equal(installed, File.ReadAllText(panel));
            Assert.False(File.Exists(Path.Combine(fm24.Game, NativeInstall.Manifest)));
            Assert.Contains(fm24.Logged, line => line.Contains("kept for your other FM24"));
        }

        [Fact]
        public void TheCompanionFm24StartsIsInAUtf16Config()
        {
            var fm24 = new Fm24(_root);
            string companion = @"C:\Users\Zoë\RegenerateFM\regeneratefm-fm24.exe";
            fm24.Install(companion);
            var data = File.ReadAllBytes(Path.Combine(fm24.Game, NativeInstall.Config));
            Assert.Equal(new byte[] { 0xFF, 0xFE }, data.Take(2));  // GetPrivateProfileStringW needs the byte-order mark.
            Assert.Equal(new Dictionary<string, string> { ["enabled"] = "1", ["port"] = "8769", ["token"] = Token, ["companion"] = companion },
                         NativeInstall.ReadConfig(Path.Combine(fm24.Game, NativeInstall.Config)).Where(entry => !entry.Key.StartsWith("[")).ToDictionary(e => e.Key, e => e.Value));
            // Without a companion (RegenerateFM from source) there is nothing for FM24 to start.
            fm24.Install();
            Assert.False(NativeInstall.ReadConfig(Path.Combine(fm24.Game, NativeInstall.Config)).ContainsKey("companion"));
            // On macOS the launcher reads the UTF-8 config line by line.
            var mac = new NativeInstall(mac: true).ConfigText(8769, Token, "/Users/Zoë/RegenerateFM/regeneratefm-fm24/regeneratefm-fm24");
            Assert.Contains("\nport=8769\ntoken=" + Token + "\n", Encoding.UTF8.GetString(mac));
            Assert.Throws<ArgumentException>(() => fm24.Native.ConfigText(8769, Token, "/tmp/a\nport=1"));
        }

        [Fact]
        public void TheSkinIsOptionalAndACompanionBuiltOneIsAdopted()
        {
            var fm24 = new Fm24(_root);
            foreach (string kind in new[] { "player", "staff" })
                File.Delete(Path.Combine(fm24.Skin, NativeInstall.PanelFiles[kind]));
            fm24.Install();
            Assert.Empty(JsonNode.Parse(File.ReadAllText(Path.Combine(fm24.Game, NativeInstall.Manifest)))["panels"].AsObject());
            Assert.True(File.Exists(fm24.Dll));
            // The companion FM24 started then built the skin with its popups.
            foreach (var (kind, text) in new[] { ("player", PlayerPanel), ("staff", StaffPanel) })
                File.WriteAllText(Path.Combine(fm24.Skin, NativeInstall.PanelFiles[kind]), Skin.ModifyPanel(text, kind));
            fm24.Install();
            var panels = JsonNode.Parse(File.ReadAllText(Path.Combine(fm24.Game, NativeInstall.Manifest)))["panels"].AsObject();
            Assert.All(panels, entry => Assert.True(entry.Value["created"].GetValue<bool>()));
            // A later release's library reinstalls over the adopted panels, which have no backup.
            fm24.Native.Install(fm24.Game, fm24.Skin, fm24.Library.Concat(new byte[] { 1 }).ToArray(), Token);
            panels = JsonNode.Parse(File.ReadAllText(Path.Combine(fm24.Game, NativeInstall.Manifest)))["panels"].AsObject();
            Assert.Equal(2, panels.Count);
            Assert.All(panels, entry => Assert.True(entry.Value["created"].GetValue<bool>()));
            fm24.Remove();
            Assert.False(File.Exists(Path.Combine(fm24.Skin, NativeInstall.PanelFiles["player"])));
        }


        [Fact]
        public void AnotherWinmmDllAndAnEditedPanelAreRefused()
        {
            var fm24 = new Fm24(_root);
            File.WriteAllBytes(fm24.Dll, Encoding.ASCII.GetBytes("MZ...someone else's..."));
            Assert.Contains("unrecognized winmm.dll", Assert.Throws<SetupException>(() => fm24.Install()).Message);
            Assert.Equal("MZ...someone else's...", File.ReadAllText(fm24.Dll));
            File.Delete(fm24.Dll);
            fm24.Install();
            File.AppendAllText(Path.Combine(fm24.Skin, NativeInstall.PanelFiles["player"]), "<!-- edit -->");
            Assert.Throws<SetupException>(() => fm24.Install());
            Assert.Throws<SetupException>(() => fm24.Remove());
        }

        [Fact]
        public void AFailedInstallOrRemovalRollsBackEveryWrite()
        {
            var fm24 = new Fm24(_root);
            var before = fm24.Snapshot();
            var written = new List<string>();
            fm24.Native.NewTransaction = () => new NativeInstall.Transaction
            {
                Write_ = (path, data, mode) =>
                {
                    written.Add(Path.GetFileName(path));
                    if (Path.GetFileName(path) == "winmm.dll")
                        throw new IOException("disk full");
                    NativeInstall.AtomicWrite(path, data, mode);
                },
            };
            Assert.Throws<IOException>(() => fm24.Install());
            Assert.Contains("winmm.dll", written);
            AssertSame(before, fm24.Snapshot());
            Assert.Contains("all files were restored", fm24.Logged.Last());
            // A clean retry is possible: no stray backups or manifest block it.
            fm24.Native.NewTransaction = () => new NativeInstall.Transaction();
            fm24.Install();
            Assert.True(File.Exists(Path.Combine(fm24.Game, NativeInstall.Manifest)));
            var installed = fm24.Snapshot();
            // A removal that fails part-way is undone too.
            fm24.Native.NewTransaction = () => new NativeInstall.Transaction
            {
                Delete_ = path =>
                {
                    if (Path.GetFileName(path) == NativeInstall.Config)
                        throw new IOException("permission denied");
                    File.Delete(path);
                },
            };
            Assert.Throws<IOException>(() => fm24.Remove());
            AssertSame(installed, fm24.Snapshot());
        }

        [Fact]
        public void TheSkinsPopupsArePostedAsFmfgWithBothRows()
        {
            Assert.Throws<ArgumentException>(() => Skin.ModifyPanel(SquadPanel, "squad"));
            string modified = Skin.ModifyPanel(PlayerPanel, "player");
            Assert.Contains("select_event=\"FMFG\"", modified);
            Assert.Contains("<record id=\"FMFG\">", modified);
            Assert.Contains("<record id=\"FMFU\">", modified);
            Assert.Contains("value=\"Update face\"", modified);
            Assert.Contains("already", Assert.Throws<ArgumentException>(() => Skin.ModifyPanel(modified, "player")).Message);
        }

        [Fact]
        public void ARunningFm26IsNotTakenForFm24()
        {
            var native = new NativeInstall(mac: false);
            // Proton's process list on Linux; fm.app's executable on the Mac.
            string executable = OperatingSystem.IsMacOS() ? @"\fm.app\Contents\MacOS\fm" : @"\fm.exe";
            var running = new List<(int, string)> { (1, @"Z:\games\steamapps\common\Football Manager 26" + executable) };
            RegenerateFM.Desktop.FM24.Memory.Processes.RunningSource = () => running;
            try
            {
                Assert.False(native.Running("/games/steamapps/common/Football Manager 2024"));
                running.Add((2, @"Z:\games\steamapps\common\Football Manager 2024" + executable));
                Assert.True(native.Running("/games/steamapps/common/Football Manager 2024"));
            }
            finally
            {
                RegenerateFM.Desktop.FM24.Memory.Processes.RunningSource = null;
            }
        }

        // ---- FM24's setup ----------------------------------------------------------

        private Fm24Setup Setup(Fm24 fm24, bool proton = false) => new Fm24Setup(fm24.Native)
        {
            BundledLibrary = () => fm24.Library, BuildSkinFromGame = (_, skin) => Directory.CreateDirectory(skin), Proton = proton,
            Log = fm24.Logged.Add,
        };

        [Fact]
        public void TheSetupKeepsTheTokenFm24AlreadyHasAndNamesTheCompanionAsWineSeesIt()
        {
            var fm24 = new Fm24(_root);
            string userData = _root.Combine("user");
            Directory.CreateDirectory(userData);
            File.WriteAllText(Path.Combine(fm24.Game, NativeInstall.Config), "token=" + Token + "\n");
            string companion = _root.Combine("RegenerateFM", "FM24", "regeneratefm-fm24.exe");
            var setup = Setup(fm24, proton: true);
            setup.Install(fm24.Game, userData, companion, launchOption: false);
            var config = NativeInstall.ReadConfig(Path.Combine(fm24.Game, NativeInstall.Config));
            Assert.Equal(Token, config["token"]);
            Assert.Equal("Z:" + companion.Replace('/', '\\'), config["companion"]);
            Assert.True(Directory.Exists(Path.Combine(userData, "graphics", "regeneratefm")));
            File.WriteAllText(Path.Combine(userData, "skins", "regeneratefm", "skin_config.xml"), Skin.SkinConfig);
            Assert.True(setup.IsSetUp(fm24.Game, Path.Combine(userData, "skins", "regeneratefm"), companion));
            // A new DLL in an update is set up again.
            Assert.False(new Fm24Setup(fm24.Native) { BundledLibrary = () => fm24.Library.Concat(new byte[] { 1 }).ToArray(), Proton = true }
                         .IsSetUp(fm24.Game, Path.Combine(userData, "skins", "regeneratefm"), companion));
            // Without a token, a new one is made.
            var fresh = new Fm24(new TempFolder());
            Setup(fresh).Install(fresh.Game, userData, null, launchOption: false);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", Fm24Setup.InstalledToken(fresh.Game));
        }

        [Fact]
        public void WithoutAGameFolderTheSetupDoesEveryFm24ItFinds()
        {
            var fm24 = new Fm24(_root);
            string userData = _root.Combine("user");
            Directory.CreateDirectory(userData);
            string steam = Path.Combine(_steam, "steamapps", "common", Fm24Setup.GameFolder);
            string epic = _root.Combine("Epic Games", Fm24Setup.EpicFolder);
            foreach (string game in new[] { steam, epic })
            {
                // fm.exe as the checked build; on the Mac fm.app is what's looked for.
                Directory.CreateDirectory(Path.Combine(game, "fm.app", "Contents", "MacOS"));
                File.WriteAllText(Path.Combine(game, Fm24Setup.MacProgram), "");
                File.Copy(Path.Combine(fm24.Game, "fm.exe"), Path.Combine(game, "fm.exe"));
            }
            EpicRecord(epic);
            Assert.Equal(new[] { SteamLaunch.Resolve(steam), epic }, Fm24Setup.FindGameDirs());
            var setup = Setup(fm24);
            Assert.Equal(0, setup.Main(new[] { "--user-data-dir", userData, "--no-launch-option" }));
            Assert.All(new[] { steam, epic }, game => Assert.True(File.Exists(Path.Combine(game, NativeInstall.Manifest))));
            // Removing goes through the copies RegenerateFM is in.
            File.Delete(Path.Combine(steam, NativeInstall.Manifest));
            File.Delete(Path.Combine(steam, NativeInstall.Config));
            File.Delete(Path.Combine(steam, "winmm.dll"));
            Assert.Equal(0, setup.Main(new[] { "--user-data-dir", userData, "--no-launch-option", "--remove" }));
            Assert.False(File.Exists(Path.Combine(epic, NativeInstall.Manifest)));
            Assert.Contains("isn't set up for any FM24",
                            Assert.Throws<SetupException>(() => setup.Main(new[] { "--user-data-dir", userData, "--no-launch-option", "--remove" })).Message);
        }

        [Fact]
        public void TheSetupRefusesWhileSteamOrFm24Runs()
        {
            var fm24 = new Fm24(_root);
            string userData = _root.Combine("user");
            Directory.CreateDirectory(userData);
            Account("1", Config.Replace("\"2252570\"\n\t\t\t\t\t{\n", "\"2252570\"\n\t\t\t\t\t{\n"));
            _steamRunning = true;
            if (!OperatingSystem.IsWindows())
                Assert.Contains("Quit Steam first", Assert.Throws<SetupException>(() => Setup(fm24).Install(fm24.Game, userData)).Message);
            _steamRunning = false;
            fm24.Native.GameRunning = _ => true;
            Assert.Contains("Close FM24", Assert.Throws<SetupException>(() => Setup(fm24).Install(fm24.Game, userData, launchOption: false)).Message);
        }

        // ---- The Linux installer ---------------------------------------------------

        private static readonly Dictionary<string, string> Fm26Files = new Dictionary<string, string>
        {
            ["winhttp.dll"] = "MZ winhttp", ["doorstop_config.ini"] = "[General]\n", [".doorstop_version"] = "4",
            ["dotnet/coreclr.dll"] = "MZ coreclr", ["BepInEx/core/BepInEx.Unity.IL2CPP.dll"] = "MZ bepinex",
            ["BepInEx/plugins/RegenerateFM/RegenerateFM.dll"] = "MZ plugin",
        };

        private sealed class Linux
        {
            public string Fm26, Fm24, Release, Data;
            public List<(int, string)> Running = new List<(int, string)>();
            public List<string[]> SetupCalls = new List<string[]>();
            public LinuxInstaller Installer;
            public List<string> Errors = new List<string>();
        }

        private Linux LinuxSetUp()
        {
            var linux = new Linux();
            string common = Path.Combine(_steam, "steamapps", "common");
            linux.Fm26 = Path.Combine(common, "Football Manager 26");
            linux.Fm24 = Path.Combine(common, "Football Manager 2024");
            foreach (string game in new[] { linux.Fm26, linux.Fm24 })
            {
                Directory.CreateDirectory(game);
                File.WriteAllText(Path.Combine(game, "fm.exe"), "MZ fm");
            }
            Account("1", Config);
            linux.Release = _root.Combine("release");
            foreach (var (name, text) in Fm26Files)
            {
                string path = Path.Combine(linux.Release, "FM26", name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text);
            }
            Directory.CreateDirectory(Path.Combine(linux.Release, "FM24"));
            File.WriteAllText(Path.Combine(linux.Release, "FM24", "regeneratefm-fm24.exe"), "MZ");
            linux.Data = _root.Combine("data");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", linux.Data);
            linux.Installer = new LinuxInstaller
            {
                Log = _ => { }, Error = linux.Errors.Add, RunningExecutables = () => linux.Running, Fm24Setup = linux.SetupCalls.Add,
            };
            return linux;
        }

        private static Dictionary<string, string> Files(string folder) =>
            Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                     .ToDictionary(path => Path.GetRelativePath(folder, path).Replace('\\', '/'), File.ReadAllText);

        private string LaunchOption(string app) => SteamLaunch.AppBlock(File.ReadAllText(Path.Combine(_steam, "userdata", "1", "config", "localconfig.vdf")), app)
                                                              ?.Value?.Value;

        [NotOnWindowsFact]
        public void Fm26IsInstalledAndRemovedWithItsLaunchOption()
        {
            var linux = LinuxSetUp();
            File.WriteAllText(Path.Combine(linux.Fm26, "winhttp.dll"), "MZ older");
            Assert.Equal(0, linux.Installer.Main(new[] { "fm26", "--release", linux.Release }, null));
            var installed = Files(linux.Fm26);
            foreach (var (name, text) in Fm26Files)
                Assert.Equal(text, installed[name]);
            // The installer is for Linux; elsewhere it sets that system's option.
            Assert.Equal(SteamLaunch.Option("fm26", SteamLaunch.Resolve(linux.Fm26)) + " %command%", LaunchOption("3551340"));
            Assert.Null(LaunchOption("2252570"));
            Assert.Empty(linux.SetupCalls);
            Assert.Equal(0, linux.Installer.Main(new[] { "fm26", "--remove", "--release", linux.Release }, null));
            Assert.Equal(new Dictionary<string, string> { ["fm.exe"] = "MZ fm" }, Files(linux.Fm26));
            Assert.Equal("", LaunchOption("3551340"));
        }

        [NotOnWindowsFact]
        public void AnotherModsPluginKeepsBepInEx()
        {
            var linux = LinuxSetUp();
            linux.Installer.Main(new[] { "fm26", "--release", linux.Release }, null);
            string other = Path.Combine(linux.Fm26, "BepInEx", "plugins", "OtherMod.dll");
            File.WriteAllText(other, "MZ other");
            linux.Installer.Main(new[] { "fm26", "--remove", "--release", linux.Release }, null);
            Assert.True(File.Exists(other));
            Assert.True(File.Exists(Path.Combine(linux.Fm26, "winhttp.dll")));
            Assert.False(Directory.Exists(Path.Combine(linux.Fm26, LinuxInstaller.Plugin)));
        }

        [NotOnWindowsFact]
        public void UnityLibrariesAreDownloadedForBepInExOnce()
        {
            var linux = LinuxSetUp();
            string header = Path.Combine(linux.Fm26, "fm_Data", "globalgamemanagers");
            Directory.CreateDirectory(Path.GetDirectoryName(header));
            File.WriteAllBytes(header, new byte[48].Concat(Encoding.ASCII.GetBytes("6000.0.52f1-fm26-05f1\0")).ToArray());
            var urls = new List<string>();
            var lines = new List<string>();
            linux.Installer.Log = lines.Add;
            UnityLibraries.Download = url =>
            {
                urls.Add(url);
                throw new HttpRequestException("timed out");
            };
            try
            {
                // Not reached: the installer says so, and BepInEx tries itself.
                Assert.Equal(0, linux.Installer.Main(new[] { "fm26", "--release", linux.Release }, null));
                Assert.Contains(lines, line => line.Contains("Couldn't download Unity's libraries") && line.Contains("timed out"));
                string zip = Path.Combine(linux.Fm26, "BepInEx", "unity-libs", "6000.0.52.zip");
                Assert.False(File.Exists(zip));
                UnityLibraries.Download = url =>
                {
                    urls.Add(url);
                    return new byte[] { (byte)'P', (byte)'K', 3, 4, 1 };
                };
                Assert.Equal(0, linux.Installer.Main(new[] { "fm26", "--release", linux.Release }, null));
                Assert.Equal(new byte[] { (byte)'P', (byte)'K', 3, 4, 1 }, File.ReadAllBytes(zip));
                // Kept on an update; BepInEx uses it rather than downloading.
                Assert.Equal(0, linux.Installer.Main(new[] { "fm26", "--release", linux.Release }, null));
                Assert.Equal(new[] { "https://unity.bepinex.dev/libraries/6000.0.52.zip" }, urls.Distinct());
                Assert.Equal(2, urls.Count);
            }
            finally
            {
                UnityLibraries.Download = _ => throw new IOException("The tests don't download.");
            }
        }

        [NotOnWindowsFact]
        public void NothingChangesWhileSteamOrTheGameRuns()
        {
            var linux = LinuxSetUp();
            _steamRunning = true;
            Assert.Contains("Quit Steam", Assert.Throws<SetupException>(() => linux.Installer.Main(new[] { "--release", linux.Release }, null)).Message);
            _steamRunning = false;
            linux.Running.Add((7, @"Z:\steam\steamapps\common\Football Manager 26\fm.exe"));
            Assert.Contains("Close Football Manager 26", Assert.Throws<SetupException>(() => linux.Installer.Main(new[] { "--release", linux.Release }, null)).Message);
            Assert.Equal(new Dictionary<string, string> { ["fm.exe"] = "MZ fm" }, Files(linux.Fm26));
            Assert.Equal(Config, File.ReadAllText(Path.Combine(_steam, "userdata", "1", "config", "localconfig.vdf")));
            Assert.Empty(linux.SetupCalls);
            // Steam may run once the launch options are set.
            linux.Running.Clear();
            linux.Installer.Main(new[] { "fm26", "--release", linux.Release }, null);
            _steamRunning = true;
            Assert.Equal(0, linux.Installer.Main(new[] { "fm26", "--release", linux.Release }, null));
        }

        [NotOnWindowsFact]
        public void EveryGameFoundIsSetUpAndTheFm24CompanionCopied()
        {
            var linux = LinuxSetUp();
            string companion = Path.Combine(linux.Data, "RegenerateFM", "FM24");
            Assert.Equal(0, linux.Installer.Main(new[] { "--release", linux.Release }, null));
            Assert.True(File.Exists(Path.Combine(linux.Fm26, "winhttp.dll")));
            Assert.Equal(new[] { "regeneratefm-fm24.exe" }, Files(companion).Keys);
            // The game folder as found in Steam's libraries (on the Mac, /var is /private/var).
            string fm24 = SteamLaunch.Resolve(linux.Fm24);
            Assert.Equal(new[] { "--game-dir", fm24, "--companion", Path.Combine(companion, "regeneratefm-fm24.exe") }, linux.SetupCalls.Single());
            File.WriteAllText(Path.Combine(linux.Fm24, NativeInstall.Manifest), "{}");
            File.WriteAllText(Path.Combine(companion, "regeneratefm-fm24.log"), "a log");
            File.WriteAllText(Path.Combine(companion, "notes.txt"), "the player's own");
            Assert.Equal(0, linux.Installer.Main(new[] { "--remove", "--release", linux.Release }, null));
            Assert.Equal(new[] { "--game-dir", fm24, "--remove" }, linux.SetupCalls.Last());
            Assert.Equal(new[] { "notes.txt" }, Files(companion).Keys);
        }

        [NotOnWindowsFact]
        public void AFailedGameDoesNotStopTheOthersAndANamedGameMustBeFound()
        {
            var linux = LinuxSetUp();
            linux.Installer.Fm24Setup = _ => throw new SetupException("FM24 isn't 24.4.2");
            Assert.Equal(1, linux.Installer.Main(new[] { "--release", linux.Release }, null));
            Assert.True(File.Exists(Path.Combine(linux.Fm26, "winhttp.dll")));
            Assert.Contains(linux.Errors, error => error.Contains("FM24 isn't 24.4.2"));
            Directory.Delete(linux.Fm26, recursive: true);
            Assert.Throws<SetupException>(() => linux.Installer.Main(new[] { "fm26", "--release", linux.Release }, null));
            Assert.Throws<SetupException>(() => linux.Installer.Main(new[] { "fm26", "fm24", "--game-dir", linux.Fm24 }, null));
            Assert.Throws<SetupException>(() => linux.Installer.Main(new[] { "fm25" }, null));
        }

        // ---- The DLL's source against the companion --------------------------------

        private static string Repository([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));

        [Fact]
        public void TheDllsAddressesAndProtocolMatchTheCompanion()
        {
            string root = Repository();
            if (!File.Exists(Path.Combine(root, "native", "fm24", "regeneratefm24.c")))
                root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
            string source = File.ReadAllText(Path.Combine(root, "native", "fm24", "regeneratefm24.c"));
            var supported = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "native", "fm24", "supported_build.json")));
            var generated = new HashSet<string>();
            foreach (var (group, prefix) in new[] { ("skinReload", "RELOAD_"), ("squadMenu", "SQUAD_MENU_"), ("dialog", "DIALOG_"), ("profileMenu", "PROFILE_MENU_") })
                foreach (var entry in supported[group].AsObject())
                    generated.Add(prefix + string.Concat(entry.Key.Select(c => char.IsUpper(c) ? "_" + c : char.ToUpperInvariant(c).ToString())));
            var used = Regex.Matches(source, @"\b((?:RELOAD|SQUAD_MENU|DIALOG|PROFILE_MENU)_[A-Z_]+_(?:RVA|OFFSET))\b").Select(match => match.Groups[1].Value).ToHashSet();
            Assert.Contains("PROFILE_MENU_TAB_BUTTON_VTABLE_RVA", used);
            Assert.Subset(generated, used);
            // Clicks name the game run, not the reused process ID; FM24's ID only tells the companion when to stop.
            Assert.Contains("(unsigned long long)bridge_instance, (long)serial", source);
            Assert.Single(Regex.Matches(source, Regex.Escape("GetCurrentProcessId()")));
            Assert.Contains("--started-by-fm24 %lu", source);
            var defines = Regex.Matches(source, @"#define (\w+)\s+(0x[0-9A-Fa-f]+)\b").ToDictionary(match => match.Groups[1].Value,
                                                                                                    match => Convert.ToInt64(match.Groups[2].Value, 16));
            Assert.Equal((Layout.Windows.SidebarVtable, (long)Layout.Windows.SidebarReference), (defines["SIDEBAR_VTABLE_RVA"], defines["SIDEBAR_OBJECT"]));
            Assert.Equal((Layout.Windows.PersonTableGlobal, (long)Layout.Windows.PersonTableSlot), (defines["DATABASE_GLOBAL_RVA"], defines["DATABASE_PEOPLE"]));
            // The DLL accepts exactly the person classes the companion does.
            Assert.Equal(Layout.Windows.PersonVtables.Keys.ToHashSet(),
                         new[] { "PLAYER_VTABLE_RVA", "NON_PLAYER_VTABLE_RVA", "PLAYER_COACH_VTABLE_RVA" }.Select(name => defines[name]).ToHashSet());
            Assert.Contains("\"REGENERATEFM24F 1 %ld %ld %s\", (long)serial, (long)person, bridge_token", source);
            Assert.Contains("\"REGENERATEFM24F %ld yes\"", source);
            Assert.Contains("\"REGENERATEFM24F %ld no\"", source);
            // The setup carries the same checked build as the DLL.
            Assert.Equal(supported["sha256"].GetValue<string>(), new NativeInstall(mac: false).Supported);
            // And Epic's fm.exe, the same code packed differently, at the same addresses.
            Assert.Equal(new[] { supported["sha256"].GetValue<string>(), "33e8a2e48c4986f2a15e11c4c93aca1199029d66b19bd765403eb22dea265378" },
                         new NativeInstall(mac: false).SupportedBuilds());
        }
    }
}
