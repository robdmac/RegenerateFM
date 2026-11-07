using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RegenerateFM.Desktop.FM24;
using RegenerateFM.Desktop.FM24.Bridge;
using RegenerateFM.Desktop.FM24.Memory;
using Xunit;

namespace RegenerateFM.Tests
{
    // The FM24 companion's face requests, with FM24's memory, the image API and
    // the DLL replaced by stand-ins. No billed request is made.
    public class CompanionTests : IDisposable
    {
        private readonly TempFolder _folder = new TempFolder();
        private readonly List<string> _calls = new List<string>();
        private readonly Companion _companion;
        private ProfileSnapshot _profile = new ProfileSnapshot { Type = "player", Name = "Test Player", Age = 25, Club = "Test Club" };
        private bool? _identityOnly;

        public CompanionTests()
        {
            Directory.CreateDirectory(_folder.Combine("graphics"));
            KeyStore.Save(KeyStore.DefaultPath, "sk-test-0123456789abcdefghij");
            _companion = new Companion
            {
                Token = new string('a', 43), GraphicsDir = _folder.Combine("graphics"), PreviewOnly = true, ReloadPort = 8770,
                Log = _ => { },
                ReadProfile = identityOnly => { _identityOnly = identityOnly; return ("123", _profile); },
                StartProgress = _ => null,
                GeneratePng = (_, prompt) => { _calls.Add("generate " + prompt); return Task.FromResult(Png()); },
                EditPng = (_, prompt, reference) => { _calls.Add("edit " + prompt); return Task.FromResult(Png(updated: true)); },
                Reload = () => _calls.Add("reload"),
                FindGame = () => throw new MemoryUnavailableException("no game in tests"),
                OpenBrowser = _ => false,
            };
        }

        public void Dispose()
        {
            File.Delete(KeyStore.DefaultPath);
            _folder.Dispose();
        }

        private static byte[] Png(bool updated = false) => Pngs.Make(256, red: (byte)(updated ? 1 : 2));

        private string[] Saved() => Directory.GetFiles(_folder.Combine("graphics"), "123-*.png");

        [Fact]
        public void APreviewMakesNoRequestAndReadsTheWholeProfile()
        {
            _profile = new ProfileSnapshot
            {
                Name = "Jak Stewart", Age = 21, Nationality = "English", Position = "Goalkeeper", Club = "Example FC", Height = "190 cm",
                Technical = new Dictionary<string, object> { ["Passing"] = 12, ["First Touch"] = 7, ["Kicking"] = 11 },
                Mental = new Dictionary<string, object> { ["Decisions"] = 14, ["Bravery"] = 5, ["Composure"] = 12 },
            };
            var result = _companion.Generate(null, "generate");
            Assert.True(result.Ok && result.Preview);
            Assert.Equal("123", result.PlayerId);
            Assert.StartsWith("Football Manager profile picture for Jak Stewart", result.Prompt);
            Assert.Empty(_calls);
            Assert.False(_identityOnly);
            Assert.Empty(Saved());
        }

        [Fact]
        public void AFaceIsSavedAndReloadedInTheGame()
        {
            _companion.PreviewOnly = false;
            _profile = new ProfileSnapshot
            {
                Name = "Jak Stewart", Age = 21, Nationality = "English", Position = "Goalkeeper", Club = "Example FC", Height = "190 cm",
                Technical = new Dictionary<string, object> { ["Passing"] = 12, ["First Touch"] = 7, ["Kicking"] = 11 },
                Mental = new Dictionary<string, object> { ["Decisions"] = 14, ["Bravery"] = 5, ["Composure"] = 12 },
            };
            var result = _companion.Generate(null, "generate");
            Assert.True(result.Refreshed, result.Error);
            Assert.Equal(new[] { Png() }, Saved().Select(File.ReadAllBytes));
            Assert.Equal(2, _calls.Count);
            Assert.StartsWith("generate Football Manager profile picture for Jak Stewart", _calls[0]);
            Assert.Equal("reload", _calls[1]);
            // The traits are saved with the faces, so the person looks the same next time.
            Assert.True(File.Exists(_folder.Combine("graphics", ".regeneratefm-appearances", "123.json")));
        }

        [Fact]
        public void AFailedReloadKeepsThePortraitAndSaysHowToLoadIt()
        {
            _companion.PreviewOnly = false;
            _profile = new ProfileSnapshot
            {
                Type = "staff", Name = "Greg Vanney", Age = 49, Nationality = "United States", JobTitle = "Manager", Club = "LA Galaxy",
            };
            Assert.True(_companion.Generate(null, "generate").Refreshed);
            string earlier = Assert.Single(Saved());
            _companion.Reload = () => throw new TimeoutException("no reply");
            var result = _companion.Generate(null, "generate");
            Assert.True(result.Ok);
            Assert.False(result.Refreshed);
            Assert.Contains("Restart FM24", result.Warning);
            // FM24 still shows the earlier face until it reloads, so that file stays.
            Assert.Equal(2, Saved().Length);
            Assert.Contains(earlier, Saved());
            _companion.Reload = () => { };
            Assert.True(_companion.Generate(null, "generate").Refreshed);
            Assert.Single(Saved());
        }

        [Fact]
        public void TheExpectedPersonIsCheckedBeforeAnyRequest()
        {
            _companion.PreviewOnly = false;
            var result = _companion.Generate("456", "generate");
            Assert.False(result.Ok);
            Assert.Contains("no image request was made", result.Error);
            Assert.Empty(_calls);
        }

        [Fact]
        public void UpdateFaceSendsTheExistingPortraitAndReadsOnlyWhoTheyAre()
        {
            _profile = new ProfileSnapshot { Name = "Lionel Messi", Age = 38, Club = "Inter Miami" };
            var photo = Pngs.Make(64);
            string asked = null;
            _companion.GameDir = "/games/fm24";
            _companion.FindExistingPortrait = (person, graphics, game) =>
            {
                asked = person + " " + graphics + " " + game;
                return (photo, "FM24 bundled messi.jpg");
            };
            var preview = _companion.Generate("123", "update");
            Assert.True(preview.Preview);
            Assert.Equal("FM24 bundled messi.jpg", preview.Reference);
            Assert.Contains("The supplied image is an earlier portrait of Lionel Messi", preview.Prompt);
            Assert.True(_identityOnly);
            Assert.Equal("123 " + _folder.Combine("graphics") + " /games/fm24", asked);
            Assert.Empty(_calls);
            _companion.PreviewOnly = false;
            var result = _companion.Generate("123", "update");
            Assert.True(result.Refreshed, result.Error);
            Assert.Contains("at age 38", _calls[0]);
            Assert.StartsWith("edit ", _calls[0]);
            Assert.Equal(new[] { Png(updated: true) }, Saved().Select(File.ReadAllBytes));
            // Without a face, Update face is refused before any request.
            _calls.Clear();
            _companion.FindExistingPortrait = (_, _, _) => null;
            var refused = _companion.Generate("123", "update");
            Assert.False(refused.Ok);
            Assert.Contains("no face to update", refused.Error);
            Assert.Empty(_calls);
        }

        [Fact]
        public void ARejectedKeyIsAskedForAgainWhereTheGameAsks()
        {
            _companion.PreviewOnly = false;
            // A companion run from a terminal: on the Mac a reload port means FM24 asks for the key.
            _companion.ReloadPort = 0;
            _profile = new ProfileSnapshot
            {
                Type = "staff", Name = "Greg Vanney", Age = 49, Nationality = "United States", JobTitle = "Manager", Club = "LA Galaxy",
            };
            _companion.GeneratePng = (_, _) => throw new ApiKeyRejectedException("OpenAI image API returned HTTP 401: Incorrect API key provided.");
            var result = _companion.Generate(null, "generate");
            Assert.False(result.Ok);
            Assert.EndsWith(Companion.RejectedKey, result.Error);  // a terminal companion: restart it
            Assert.Empty(Saved());
            // Without a key at all, nothing is requested.
            File.Delete(KeyStore.DefaultPath);
            Assert.Equal(Companion.MissingKey, _companion.Generate(null, "generate").Error);
        }

        [Fact]
        public void TheProgressDialogShowsTheErrorSoNothingElseNeedsTo()
        {
            _companion.PreviewOnly = false;
            var progress = new FakeProgress();
            _companion.StartProgress = _ => progress;
            _companion.ReadProfile = _ => throw new InvalidOperationException("The open FM24 profile changed during capture; no image request was made.");
            var result = _companion.Generate(null, "generate");
            Assert.True(result.Shown);
            Assert.Equal(new[] { "fail The open FM24 profile changed during capture; no image request was made." }, progress.Events);
        }

        private sealed class FakeProgress : IFaceProgress
        {
            public readonly List<string> Events = new List<string>();
            public bool Failed { get; private set; }
            public void Loading() => Events.Add("loading");
            public bool Fail(string message) { Failed = true; Events.Add("fail " + message); return true; }
            public void Close() => Events.Add("close");
        }

        [Theory]
        [InlineData("yes", true)]
        [InlineData("no", false)]
        public void ANewerReleaseIsOfferedOnceFm24CanAsk(string answer, bool opens)
        {
            // Still starting: the first try is turned away, and it tries again.
            using var dll = new FakeDll("busy", "queued", "shown", "shown", answer);
            var opened = new List<string>();
            var companion = new Companion { ReloadPort = dll.Port, Token = new string('t', 43), Log = _ => { }, OpenBrowser = url => { opened.Add(url); return true; } };
            companion.OfferUpdate("1.0.2", TimeSpan.Zero);
            string asked = Encoding.UTF8.GetString(Convert.FromBase64String(dll.Received.Last()[6]));
            Assert.Equal("RegenerateFM 1.0.2 is available. Open the download page?", asked);
            Assert.Equal(opens ? new[] { UpdateCheck.ReleasesPage } : new string[0], opened);
        }

        [Fact]
        public void NoNewerReleaseAsksNothing()
        {
            using var dll = new FakeDll();
            new Companion { ReloadPort = dll.Port, Token = new string('t', 43), Log = _ => { } }.OfferUpdate(null, TimeSpan.Zero);
            Assert.Empty(dll.Received);
        }

        [Fact]
        public void ProgressIsOnThePortraitPopupElseInADialog()
        {
            using (var dll = new FakeDll("done"))
            {
                var companion = new Companion { ReloadPort = dll.Port, Token = new string('t', 43), Log = _ => { } };
                Assert.IsType<PortraitProgress>(companion.StartProgress("update"));
                Assert.Equal(("REGENERATEFM24P", "portrait"), (dll.Received[0][0], dll.Received[0][4]));
                Assert.Equal("Updating…", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(dll.Received[0][5])));
            }
            using (var dll = new FakeDll())
            {
                // Without RegenerateFM's skin there is no popup: a dialog shows progress and errors.
                dll.Answer = fields => fields[0] == "REGENERATEFM24P" && fields[4] == "portrait" ? "failed"
                                     : fields[0] == "REGENERATEFM24P" ? "done" : "shown";
                var companion = new Companion { ReloadPort = dll.Port, Token = new string('t', 43), Log = _ => { } };
                var shown = companion.StartProgress("generate");
                Assert.IsType<DialogProgress>(shown);
                shown.Loading();
                Assert.True(shown.Fail("OpenAI image API returned HTTP 500."));
                var sent = dll.Received.Where(fields => fields[0] != "REGENERATEFM24P" || fields[4] != "portrait").ToList();
                Assert.Equal("Generating a new face…", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(sent[0][6])));
                Assert.Equal(new[] { "dialog", "finish" }, sent.Skip(1).Where(fields => fields[0] == "REGENERATEFM24P").Select(fields => fields[4]).Distinct());
            }
        }

        [Fact]
        public void TheOpenProfileIsReadTwiceAndAChangeIsRefused()
        {
            // A whole FM24: the UI manager, a profile screen with its header,
            // subtitle and biography, and the person's record.
            long record = 0x10000;
            var game = MemoryTests.Player(new FakeProcess(), record, 7458500, "Lionel Messi", position: 12, born: 1987, day: 175);
            MemoryTests.Team(game, 0x40000, 77, null, 77, "Inter Miami");
            MemoryTests.Contract(game, record, 0x40000);
            long manager = 0x60000, stack = 0x61000, root = 0x62000, head = 0x63000, subtitle = 0x64000, biography = 0x65000, age = 0x66000;
            game.Put(0x60000, new byte[0x8000]);
            game.Q(MemoryTests.Image + Layout.Windows.ManagerGlobal, 0x67000).Q(0x67000 + 0x68, stack).Q(stack + 0x18, 0x68000, 0x68008).Q(0x68000, manager);
            void Widget(long widget, string id, params long[] children)
            {
                game.Q(widget + Widgets.Manager, manager).I(widget + Widgets.Id, (uint)Widgets.SkinId(id))
                    .Q(widget + Widgets.Children, widget + 0x200, widget + 0x200 + 8 * children.Length).Q(widget + 0x200, children);
            }
            game.Q(manager + ScreenReader.ScreenRoot, root);
            Widget(root, "ROOT", head, biography, age);
            Widget(head, "head", subtitle);
            Widget(subtitle, "subs");
            Widget(biography, "biot");
            Widget(age, "text");
            game.Q(subtitle + ScreenReader.SubtitleText, 0x69000).Text(0x69000, "Striker (Center) - Inter Miami (ID:7458500)");
            game.Q(biography + ScreenReader.BiographyText, 0x6A000).Text(0x6A000, "Barcelona legend Lionel Messi is one of the greats.\n\nMore.");
            game.Q(age + ScreenReader.WidgetText, 0x6B000).Text(0x6B000, "35 years old");
            _companion.FindGame = () => game;
            var (person, profile) = _companion.ReadNativeProfile(identityOnly: false);
            Assert.Equal(("7458500", "Lionel Messi", 35, "Inter Miami", "Striker (Center)"), (person, profile.Name, profile.Age, profile.Club, profile.Position));
            Assert.Equal("Barcelona legend Lionel Messi is one of the greats.", profile.BiographyHeadline);
            var (_, identity) = _companion.ReadNativeProfile(identityOnly: true);
            Assert.Null(identity.Nationality);
            // The header shows another person: refused.
            game.Text(0x69000, "Striker (Center) - Inter Miami (ID:2000045865)");
            Assert.Contains("from game memory", Assert.Throws<InvalidOperationException>(() => _companion.ReadNativeProfile(false)).Message);
        }

        [Fact]
        public void TheKeyWindowSavesACheckedKeyOrCredits()
        {
            File.Delete(KeyStore.DefaultPath);
            var asked = new Queue<string>(new[] { "my key", "sk-wrong-0123456789abcdef", "sk-good-0123456789abcdef" });
            var shown = new List<string>();
            _companion.ReloadPort = 0;
            _companion.AskInWindow = text => { shown.Add(text); return asked.Dequeue(); };
            _companion.CheckKey = key => key == "sk-good-0123456789abcdef" ? null : ImageApi.KeyRejected;
            Assert.True(_companion.AskKeyInWindow());
            Assert.Equal("sk-good-0123456789abcdef", KeyStore.Load(KeyStore.DefaultPath));
            Assert.Contains("starts with sk-", shown[1]);
            Assert.StartsWith(ImageApi.KeyRejected, shown[2]);
            Assert.Contains(Credits.BuyText, shown[0]);
            // With the key saved and accepted, it isn't asked for again; once rejected, it is.
            Assert.True(_companion.AskKeyInWindow());
            Assert.Equal(3, shown.Count);
            _companion.KeyRejected = true;
            _companion.AskInWindow = text => { shown.Add(text); return null; };
            Assert.False(_companion.AskKeyInWindow());
            Assert.StartsWith("OpenAI didn't accept the saved key.", shown.Last());
        }

        [Fact]
        public void BuyCreditsSignsInAndSavesTheTokenWhereTheKeyGoes()
        {
            using var credits = new StandIn();
            File.Delete(KeyStore.DefaultPath);
            File.WriteAllText(Settings.DefaultPath, "[Credits]\nServiceUrl = " + credits.Url + "\n");
            try
            {
                var told = new List<string>();
                Assert.Null(_companion.SignInWithCredits(told.Add));
                Assert.Equal(credits.Token, KeyStore.Load(KeyStore.DefaultPath));
                Assert.Contains("WXYZ-K7P3", told[0]);
                Assert.Contains("Signed in to RegenerateFM credits as player@example.com, with 75 credits.", told[1]);
                credits.Expire = true;
                Assert.Contains("expired", _companion.SignInWithCredits(_ => { }));
            }
            finally
            {
                File.Delete(Settings.DefaultPath);
            }
        }
    }
}
