using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace RegenerateFM.Tests
{
    // Core: the image requests, the key, the faces folder and its files, as the
    // FM26 plugin and the FM24 companion use them.
    public class CoreTests
    {
        private static void Wait(System.Threading.Tasks.Task task) => task.GetAwaiter().GetResult();

        private static ProfileSnapshot Player() => new ProfileSnapshot
        {
            Name = "Jak Stewart", Age = 21, Nationality = "English", Position = "Goalkeeper", Club = "Example FC", Height = "190 cm",
            Technical = new Dictionary<string, object> { ["Passing"] = 12, ["First Touch"] = 7, ["Kicking"] = "11-14" },
            Mental = new Dictionary<string, object> { ["Decisions"] = 14, ["Bravery"] = 5, ["Composure"] = "12–12" },
            Goalkeeping = new Dictionary<string, object> { ["Reflexes"] = 18, ["Handling"] = 17, ["One On Ones"] = 4 },
        };

        [Fact]
        public void AnUncheckedBuildIsNamedByItsAppInfo()
        {
            var fm27 = GameInfo.FromAppInfo("Sports Interactive\nFootball Manager 27");
            Assert.Equal((27, false, "Football Manager 27", "FM27"), (fm27.Number, fm27.Demo, fm27.UserDataName, fm27.ShortName));
            var demo = GameInfo.FromAppInfo("Sports Interactive\r\nFootball Manager 26 Demo\r\n");
            Assert.Equal((26, true, "Football Manager 26 Demo"), (demo.Number, demo.Demo, demo.UserDataName));
            Assert.Equal(28, GameInfo.FromAppInfo("Sports Interactive\nFootball Manager 28").Number);
            // Not a game the plugin is for.
            Assert.Null(GameInfo.FromAppInfo("Sports Interactive\nFootball Manager 2024"));
            Assert.Null(GameInfo.FromAppInfo("Sports Interactive\nFootball Manager 25"));
            Assert.Null(GameInfo.FromAppInfo("Someone Else\nFootball Manager 27"));
            Assert.Null(GameInfo.FromAppInfo(""));
            Assert.Null(GameInfo.FromAppInfo(null));
        }

        [Fact]
        public void TextReadInPlaceOfANameMakesNoFace()
        {
            // FM27's banner once gave the squad number where the name was read.
            Assert.False(GameInfo.PlausibleName("4"));
            Assert.False(GameInfo.PlausibleName(" "));
            Assert.True(GameInfo.PlausibleName("Li Liming"));
            Assert.True(GameInfo.PlausibleName("Ødegaard"));
            var player = Player();
            player.Name = "4";
            Assert.Throws<ArgumentException>(() => Prompts.UpdatePrompt(player));
        }

        [Fact]
        public void OnlyMenAreGivenAFacialHairChange()
        {
            // Across many people, about half of men get the change; women and
            // people whose gender the game didn't give never do.
            int men = 0;
            for (int id = 1; id <= 200; id++)
            {
                var person = new ProfileSnapshot { Name = "Sam Taylor", Age = 31, Club = "Example FC", Position = "Striker" };
                person.Gender = "male";
                if (Prompts.UpdatePrompt(person, id.ToString()).Contains("Change their facial hair"))
                    men++;
                foreach (var gender in new[] { "female", null })
                {
                    person.Gender = gender;
                    string prompt = Prompts.UpdatePrompt(person, id.ToString());
                    Assert.DoesNotContain("Change their facial hair", prompt);
                    Assert.Contains("Do not add any facial hair.", prompt);
                }
            }
            Assert.InRange(men, 70, 130);
        }

        [Fact]
        public void EveryTranslationKeepsItsEnglishPlaceholders()
        {
            var assembly = typeof(Text).Assembly;
            var files = assembly.GetManifestResourceNames().Where(name => name.StartsWith("Translations/")).ToList();
            Assert.NotEmpty(files);
            var placeholder = new Regex(@"\{(\d+)\}");
            foreach (var file in files)
            {
                using var stream = assembly.GetManifestResourceStream(file);
                using var document = JsonDocument.Parse(stream);
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    string Holes(string text) => string.Join(",", placeholder.Matches(text).Select(match => match.Value).OrderBy(hole => hole));
                    Assert.True(Holes(entry.Name) == Holes(entry.Value.GetString()), file + ": " + entry.Name + " → " + entry.Value);
                    // Each one formats as the English does.
                    string.Format(entry.Value.GetString(), Enumerable.Range(0, 9).Select(i => (object)i).ToArray());
                }
            }
        }

        [Fact]
        public void TextFallsBackToEnglish()
        {
            Assert.Equal("Update face", Text.In("english (us)", "Update face"));
            Assert.Equal("Not a key at all", Text.In("german", "Not a key at all"));
            Assert.Equal("Not a key at all", Text.In("klingon", "Not a key at all"));
            Assert.NotEqual("Update face", Text.In("german", "Update face"));
            // A regional language uses its base language's text where it has none.
            Assert.Equal(Text.In("spanish", "Update face"), Text.In("spanish (mexico)", "Update face"));
            Assert.Equal("spanish-latam.json", Text.FileName("spanish (latam)"));
        }

        [Fact]
        public void TheGameIsNamedAsTheOneRunning()
        {
            var language = Text.Language;
            try
            {
                Text.Language = "english";
                Text.GameName = "FM27";
                Assert.Equal("Restart FM27 to load it.", Text.T("Restart FM26 to load it."));
                Assert.Equal("FM27 2", Text.T("FM26 {0}", 2));
                Text.GameName = null;
                Assert.Equal("Restart FM26 to load it.", Text.T("Restart FM26 to load it."));
            }
            finally
            {
                Text.GameName = null;
                Text.Language = language;
            }
        }

        [Fact]
        public void PortraitsAreScaledToFmsSizeKeepingTransparency()
        {
            var fitted = ImageApi.FitPortrait(Pngs.Make(1024));
            var image = Png.Decode(fitted);
            Assert.Equal((256, 256), (image.Width, image.Height));
            Assert.Equal(0, image.Rgba[3]);                           // a transparent corner stays transparent
            Assert.Equal(255, image.Rgba[(128 * 256 + 128) * 4 + 3] > 100 ? 255 : 0);
            var small = Pngs.Make(256);
            Assert.Same(small, ImageApi.FitPortrait(small));
        }

        [Fact]
        public void EachFaceGetsANewFileAndConfigMapsItToThePerson()
        {
            using var folder = new TempFolder();
            string graphics = folder.Combine("graphics");
            var png = Pngs.Make(256);
            Assert.Null(Portraits.CurrentPortrait(graphics, "5"));
            string first = Portraits.SavePortrait(graphics, "5", png);
            string second = Portraits.SavePortrait(graphics, "5", png);
            Assert.Matches(@"^5-[0-9a-f]{16}\.png$", Path.GetFileName(first));
            Assert.NotEqual(first, second);
            Assert.False(File.Exists(first));  // replaced by the second
            Assert.NotNull(Portraits.CurrentPortrait(graphics, "5"));
            var records = XDocument.Load(Path.Combine(graphics, "config.xml")).Descendants("record")
                                   .Where(record => record.Attribute("to") != null)
                                   .Select(record => ((string)record.Attribute("from"), (string)record.Attribute("to"))).OrderBy(r => r.Item2).ToList();
            string stem = Path.GetFileNameWithoutExtension(second);
            Assert.Equal(new[] { (stem, "graphics/pictures/person/5/portrait"), (stem, "graphics/pictures/person/r-5/portrait") }, records);
            File.WriteAllText(Path.Combine(graphics, "config.xml"), "<record><list id='other'/></record>");
            var refused = Assert.Throws<ArgumentException>(() => Portraits.SavePortrait(graphics, "9", png));
            Assert.Equal("Existing config.xml has no maps list; refusing to alter it.", refused.Message);
            Assert.Empty(Directory.GetFiles(graphics, "9*.png"));
            Assert.Throws<ArgumentException>(() => Portraits.SavePortrait(graphics, "../9", png));
        }

        [Fact]
        public void PreviousFaceBringsBackEarlierFacesInTurn()
        {
            using var folder = new TempFolder();
            string graphics = folder.Combine("graphics");
            byte[] Face(byte red) => Pngs.Make(64, red);
            var made = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            Portraits.SavePortrait(graphics, "5", Face(10), owner: "Sean Winton", made: made);
            Portraits.SavePortrait(graphics, "5", Face(20), owner: "Sean Winton", made: made.AddMinutes(1));
            Portraits.SavePortrait(graphics, "5", Face(30), owner: "Sean Winton", made: made.AddMinutes(2));
            Assert.Single(Directory.GetFiles(graphics, "5-*.png"));
            Assert.Equal(2, FaceHistory.EarlierCount(graphics, "5", "Sean Winton"));
            Assert.Equal(0, FaceHistory.EarlierCount(graphics, "5", "Gerardo Morales"));
            // Back one at a time, then round to the newest again.
            foreach (byte red in new byte[] { 20, 10, 30, 20 })
            {
                string saved = FaceHistory.Previous(graphics, "5", "Sean Winton");
                Assert.Equal(Face(red), File.ReadAllBytes(saved));
                Assert.Equal(Face(red), Portraits.CurrentPortrait(graphics, "5"));
                Assert.Single(Directory.GetFiles(graphics, "5-*.png"));
                Assert.Equal(2, FaceHistory.EarlierCount(graphics, "5", "Sean Winton"));
            }
            Assert.Null(FaceHistory.Previous(graphics, "5", "Gerardo Morales"));
        }

        [Fact]
        public void TheHistoryKeepsTheNewestFacesOfEachOwner()
        {
            using var folder = new TempFolder();
            string graphics = folder.Combine("graphics");
            var made = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < FaceHistory.KeptPerOwner + 3; i++)
                Portraits.SavePortrait(graphics, "5", Pngs.Make(64, (byte)i), owner: "Sean Winton", made: made.AddMinutes(i));
            Assert.Equal(FaceHistory.KeptPerOwner, FaceHistory.EarlierCount(graphics, "5", "Sean Winton"));
            Assert.Equal(FaceHistory.KeptPerOwner, Directory.GetFiles(Path.Combine(graphics, ".regeneratefm-history", "5"), "*.face").Length);
        }

        [Fact]
        public void RemoveFaceShowsTheGamesOwnAndKeepsItForPrevious()
        {
            using var folder = new TempFolder();
            string graphics = folder.Combine("graphics");
            Assert.False(FaceHistory.Remove(graphics, "5"));
            var made = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            Portraits.SavePortrait(graphics, "5", Pngs.Make(64, 10), owner: "Sean Winton", made: made);
            Portraits.SavePortrait(graphics, "5", Pngs.Make(64, 20), owner: "Sean Winton", made: made.AddMinutes(1));
            Assert.True(FaceHistory.Remove(graphics, "5"));
            Assert.Null(Portraits.CurrentPortrait(graphics, "5"));
            Assert.Empty(Directory.GetFiles(graphics, "5-*.png"));
            Assert.DoesNotContain("person/5/", File.ReadAllText(Path.Combine(graphics, "config.xml")));
            // Seeing the person again (in a later session) brings back none of
            // their faces: they took the face off, not just the one shown.
            Assert.False(FaceHistory.Claim(graphics, "5", "Sean Winton"));
            Assert.Null(Portraits.CurrentPortrait(graphics, "5"));
            Assert.NotNull(FaceHistory.Previous(graphics, "5", "Sean Winton"));
            Assert.Equal(Pngs.Make(64, 20), Portraits.CurrentPortrait(graphics, "5"));
            // The face they chose again is theirs to keep: another save's
            // person moves it aside, and seeing them again brings it back.
            Assert.True(FaceHistory.Claim(graphics, "5", "Gerardo Morales"));
            Assert.True(FaceHistory.Claim(graphics, "5", "Sean Winton"));
            Assert.Equal(Pngs.Make(64, 20), Portraits.CurrentPortrait(graphics, "5"));
        }

        [Fact]
        public void AFaceMadeInAnotherSaveMakesWayForThisSavesPerson()
        {
            using var folder = new TempFolder();
            string graphics = folder.Combine("graphics");
            // One save's Sean Winton has ID 5; another save gives 5 to Gerardo Morales.
            Portraits.SavePortrait(graphics, "5", Pngs.Make(64, 10), owner: "Sean Winton");
            Assert.False(FaceHistory.Claim(graphics, "5", "Sean Winton"));
            Assert.True(FaceHistory.Claim(graphics, "5", "Gerardo Morales"));
            Assert.Null(Portraits.CurrentPortrait(graphics, "5"));
            Assert.False(FaceHistory.Claim(graphics, "5", "Gerardo Morales"));
            Portraits.SavePortrait(graphics, "5", Pngs.Make(64, 20), owner: "Gerardo Morales");
            // Back in the first save, each gets their own face again.
            Assert.True(FaceHistory.Claim(graphics, "5", "Sean Winton"));
            Assert.Equal(Pngs.Make(64, 10), Portraits.CurrentPortrait(graphics, "5"));
            Assert.Equal("Sean Winton", FaceHistory.Owner(graphics, "5"));
            Assert.True(FaceHistory.Claim(graphics, "5", "Gerardo Morales"));
            Assert.Equal(Pngs.Make(64, 20), Portraits.CurrentPortrait(graphics, "5"));
            // Previous face is only ever one of the person's own.
            Assert.Null(FaceHistory.Previous(graphics, "5", "Gerardo Morales"));
            // A face with no recorded owner is left alone.
            Assert.False(FaceHistory.Claim(graphics, "7", "Sean Winton"));
            Portraits.SavePortrait(graphics, "7", Pngs.Make(64, 30));
            Assert.False(FaceHistory.Claim(graphics, "7", "Sean Winton"));
            Assert.NotNull(Portraits.CurrentPortrait(graphics, "7"));
            // Previous face offers it to anyone with the ID, as it may be theirs.
            Portraits.SavePortrait(graphics, "7", Pngs.Make(64, 40), owner: "Sean Winton");
            Assert.Equal(Pngs.Make(64, 30), File.ReadAllBytes(FaceHistory.Previous(graphics, "7", "Sean Winton")));
        }

        [Fact]
        public void KnownEthnicitiesAreSavedAndTheUsersChoiceKept()
        {
            using var folder = new TempFolder();
            KnownEthnicity Known(string ethnicity, string source, string skin = null, string hair = null) =>
                new KnownEthnicity { Ethnicity = ethnicity, Source = source, SkinTone = skin, HairColour = hair };
            Assert.Equal(2, KnownEthnicities.Save(folder.Path, new Dictionary<string, KnownEthnicity>
            {
                ["28122642"] = Known("AfricanCaribbean", "match", "African8", "dark brown"),
                ["2000047230"] = Known("AfricanCaribbean", "chosen"),
            }));
            // A match never replaces the user's choice, and the same value counts as no change.
            Assert.Equal(0, KnownEthnicities.Save(folder.Path, new Dictionary<string, KnownEthnicity>
            {
                ["2000047230"] = Known("NorthernEuropean", "match", "White1"),
                ["28122642"] = Known("AfricanCaribbean", "match", "African8", "dark brown"),
            }));
            Assert.Throws<ArgumentException>(() => KnownEthnicities.Save(folder.Path, new Dictionary<string, KnownEthnicity>
            {
                ["1"] = Known("Martian", "match"),
            }));
            // A newgen's look replaces a match; a match never replaces it.
            Assert.Equal(1, KnownEthnicities.Save(folder.Path, new Dictionary<string, KnownEthnicity> { ["36099373"] = Known("NorthernEuropean", "match") }));
            Assert.Equal(1, KnownEthnicities.Save(folder.Path, new Dictionary<string, KnownEthnicity>
            {
                ["36099373"] = Known("AfricanCaribbean", "newgen", "African5"), ["2000047230"] = Known("EastAsian", "newgen"),
            }));
            Assert.Equal(0, KnownEthnicities.Save(folder.Path, new Dictionary<string, KnownEthnicity> { ["36099373"] = Known("NorthernEuropean", "match") }));
            var player = Player();
            Assert.True(KnownEthnicities.Apply(player, folder.Path, "28122642"));
            Assert.Contains("Ethnicity: Black African or Caribbean, with dark brown skin and naturally dark brown hair; " +
                            "follow this over any other appearance detail.", Prompts.ProfilePrompt(player, "28122642", null));
            var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.Path, KnownEthnicities.FileName))).RootElement.GetProperty("people");
            Assert.Equal("{\"ethnicity\":\"AfricanCaribbean\",\"source\":\"chosen\"}", saved.GetProperty("2000047230").GetRawText().Replace(" ", "").Replace("\r", "").Replace("\n", ""));
            // An unreadable file counts as no saved ethnicities.
            File.WriteAllText(Path.Combine(folder.Path, KnownEthnicities.FileName), "{not json");
            Assert.False(KnownEthnicities.Apply(Player(), folder.Path, "28122642"));
        }

        [Fact]
        public void ImageRequestsAreOpenAIsAndRefusedKeysSayWhy()
        {
            using var api = new StandIn();
            var settings = new ImageSettings { ApiKey = "sk-test", Quality = "low", BaseUrl = api.Url + "/v1" };
            var png = ImageApi.GeneratePng(settings, "A face").GetAwaiter().GetResult();
            Assert.Equal(256, Png.Decode(png).Width);
            var reference = ImageApi.FitPortrait(Pngs.Make(512));
            ImageApi.EditPng(new ImageSettings { ApiKey = "sk-test", BaseUrl = api.Url + "/v1" }, "Update", reference).GetAwaiter().GetResult();
            var rejected = Assert.Throws<ApiKeyRejectedException>(() =>
                ImageApi.GeneratePng(new ImageSettings { ApiKey = "sk-bad-key", BaseUrl = api.Url + "/v1" }, "A face").GetAwaiter().GetResult());
            Assert.Equal("OpenAI image API returned HTTP 401: Incorrect API key provided: [REDACTED].", rejected.Message);
            Assert.Throws<MissingApiKeyException>(() => Wait(ImageApi.GeneratePng(new ImageSettings { ApiKey = "", BaseUrl = api.Url }, "x")));
            Assert.Contains("Quality", Assert.Throws<ArgumentException>(() =>
                Wait(ImageApi.GeneratePng(new ImageSettings { ApiKey = "sk-test", Quality = "best", BaseUrl = api.Url }, "x"))).Message);
            var requests = api.Received;
            Assert.Equal(3, requests.Count);
            Assert.Equal(("/v1/images/generations", "Bearer sk-test", "application/json"),
                         (requests[0].Path, requests[0].Authorization, requests[0].ContentType.Split(';')[0]));
            var body = JsonDocument.Parse(requests[0].Body).RootElement;
            Assert.Equal(new Dictionary<string, string>
            {
                ["model"] = "gpt-image-2.5-sunburst", ["size"] = "1024x1024", ["quality"] = "low", ["background"] = "transparent",
                ["output_format"] = "png", ["prompt"] = "A face",
            }, body.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString()));
            Assert.Equal(("/v1/images/edits", "multipart/form-data"), (requests[1].Path, requests[1].ContentType.Split(';')[0]));
            string form = Encoding.Latin1.GetString(requests[1].Body).Replace("\"", "");
            foreach (string name in new[] { "model", "size", "quality", "background", "output_format", "prompt", "image[]" })
                Assert.Contains("name=" + name, form);
            Assert.True(requests[1].Body.AsSpan().IndexOf(reference) >= 0);
            Assert.All(requests, request => Assert.Equal("RegenerateFM", request.Agent));
        }

        [Fact]
        public void TheKeyIsCheckedForFreeAndKeptOutsideTheGameFolder()
        {
            using var api = new StandIn();
            using var folder = new TempFolder();
            string path = folder.Combine("RegenerateFM", "openai-key");
            var settings = new ImageSettings { BaseUrl = api.Url + "/v1" };
            Assert.Null(KeyStore.Load(path));
            Assert.Null(ImageApi.CheckKey(settings, api.GoodKey).GetAwaiter().GetResult());
            Assert.Equal(ImageApi.KeyRejected, ImageApi.CheckKey(settings, "sk-wrong-key-0123456789abcdef").GetAwaiter().GetResult());
            KeyStore.Save(path, "  " + api.GoodKey + "\n");
            Assert.Equal(api.GoodKey, KeyStore.Load(path));
            // Encrypted for the user on Windows; elsewhere marked unencrypted and readable only by its owner.
            Assert.Equal(OperatingSystem.IsWindows() ? "RFMK1\n" : "RFMK0\n", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 6));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Contains("starts with sk-", Assert.Throws<ArgumentException>(() => KeyStore.Save(path, "my key")).Message);
            Assert.Equal("Paste your OpenAI API key first.", Assert.Throws<ArgumentException>(() => KeyStore.Save(path, "")).Message);
            Assert.Equal(api.GoodKey, KeyStore.Load(path));
            Assert.Equal(new[] { "/v1/models", "/v1/models" }, api.Received.Select(request => request.Path));
        }

        [Fact]
        public void UpdateFaceStartsFromTheGamesPictureThenRegenerateFMsOwn()
        {
            using var api = new StandIn();
            using var folder = new TempFolder();
            string graphics = folder.Combine("graphics");
            var settings = new ImageSettings { ApiKey = "sk-test", BaseUrl = api.Url + "/v1" };
            var player = new ProfileSnapshot { Name = "Lionel Messi", Age = 38, Club = "Inter Miami" };
            var shown = ImageApi.FitPortrait(Pngs.Make(512, red: 10));
            var none = Assert.Throws<InvalidOperationException>(() =>
                Portraits.MakePortrait(settings, graphics, "7458500", player, update: true, shownPng: null).GetAwaiter().GetResult());
            Assert.Equal("This person has no face to update yet. Use Generate face instead.", none.Message);
            string first = Portraits.MakePortrait(settings, graphics, "7458500", player, true, shown).GetAwaiter().GetResult();
            var saved = File.ReadAllBytes(first);
            Portraits.MakePortrait(settings, graphics, "7458500", player, true, shown).GetAwaiter().GetResult();
            var staff = new ProfileSnapshot
            {
                Type = "staff", Name = "Alex Morgan", Age = 45, Nationality = "Wales", JobTitle = "Assistant Manager",
                BiographyHeadline = "Morgan started coaching in 2021.",
            };
            string staffPath = Portraits.MakePortrait(settings, graphics, "5118334", staff, false, null).GetAwaiter().GetResult();
            var before = File.ReadAllBytes(staffPath);
            Assert.Contains("401", Assert.Throws<ApiKeyRejectedException>(() =>
                Portraits.MakePortrait(new ImageSettings { ApiKey = "sk-bad-key", BaseUrl = api.Url + "/v1" }, graphics, "5118334", staff, false, null)
                         .GetAwaiter().GetResult()).Message);
            staff.Age = 3;
            Assert.Equal("Staff age is missing or invalid.", Assert.Throws<ArgumentException>(() =>
                Portraits.MakePortrait(settings, graphics, "5118334", staff, false, null).GetAwaiter().GetResult()).Message);
            Assert.Equal(before, File.ReadAllBytes(staffPath));
            var requests = api.Received;
            Assert.Equal(new[] { "/v1/images/edits", "/v1/images/edits", "/v1/images/generations", "/v1/images/generations" },
                         requests.Select(request => request.Path));
            Assert.True(requests[0].Body.AsSpan().IndexOf(shown) >= 0);
            Assert.False(requests[1].Body.AsSpan().IndexOf(shown) >= 0);
            Assert.True(requests[1].Body.AsSpan().IndexOf(saved) >= 0);
            Assert.Contains("at age 38", Encoding.Latin1.GetString(requests[0].Body));
            Assert.Contains("Morgan started coaching in 2021.", JsonDocument.Parse(requests[2].Body).RootElement.GetProperty("prompt").GetString());
        }

        [Fact]
        public void AnFm26NewgenFaceBecomesAPhotographAndARelativesFaceIsTheReference()
        {
            using var api = new StandIn();
            using var folder = new TempFolder();
            string graphics = folder.Combine("graphics");
            var settings = new ImageSettings { ApiKey = "sk-test", BaseUrl = api.Url + "/v1" };
            var newgen = new ProfileSnapshot { Name = "Craig Griffiths", Age = 16, Club = "Arsenal", Gender = "male" };
            var shown = ImageApi.FitPortrait(Pngs.Make(512));
            Portraits.MakePortrait(settings, graphics, "2002068539", newgen, true, shown, shownNewgen: true).GetAwaiter().GetResult();
            Portraits.MakePortrait(settings, graphics, "2002068539", newgen, true, shown, shownNewgen: true).GetAwaiter().GetResult();
            Assert.True(File.Exists(Path.Combine(graphics, ".regeneratefm-appearances", "2002068539.json")));
            var requests = api.Received;
            string firstForm = Encoding.UTF8.GetString(requests[0].Body), secondForm = Encoding.UTF8.GetString(requests[1].Body);
            Assert.Contains(Prompts.NewgenPrompt(newgen, "2002068539", graphics), firstForm);
            Assert.Contains("computer-generated 3D face for Craig Griffiths", firstForm);
            // Once RegenerateFM has a face for them, Update face refreshes that photograph instead.
            Assert.Contains(Prompts.UpdatePrompt(newgen, "2002068539"), secondForm);
            Assert.DoesNotContain("computer-generated", secondForm);

            var father = new ProfileSnapshot
            {
                Name = "Marco Ricci", Age = 36, Nationality = "Italy", Position = "Centre-back", Club = "Genoa", Height = "6'2\"",
                Technical = new Dictionary<string, object> { ["Heading"] = 16, ["Tackling"] = 15, ["Marking"] = 16 },
                Mental = new Dictionary<string, object> { ["Leadership"] = 18, ["Positioning"] = 17, ["Aggression"] = 14 },
            };
            ProfileSnapshot Son(string name) => new ProfileSnapshot
            {
                Name = name, Age = 18, Nationality = "Italy", Position = "Centre-back", Club = "Genoa", Height = "6'2\"",
                Technical = father.Technical, Mental = father.Mental,
                Relatives = new List<(string, string)> { ("Team-mates", "9"), ("Father", "1001") },
            };
            Portraits.MakePortrait(settings, graphics, "1002", Son("Luca Ricci"), false, null).GetAwaiter().GetResult();
            string fatherFace = Portraits.MakePortrait(settings, graphics, "1001", father, false, null).GetAwaiter().GetResult();
            var fatherPng = File.ReadAllBytes(fatherFace);
            Portraits.MakePortrait(settings, graphics, "1003", Son("Gio Ricci"), false, null).GetAwaiter().GetResult();
            requests = api.Received;
            Assert.Equal(new[] { "/v1/images/generations", "/v1/images/generations", "/v1/images/edits" },
                         requests.Skip(2).Select(request => request.Path));
            Assert.True(requests[4].Body.AsSpan().IndexOf(fatherPng) >= 0);
            Assert.Contains("a portrait of Gio Ricci's father, not of Gio Ricci", Encoding.Latin1.GetString(requests[4].Body));
        }

        [Fact]
        public void SettingsAreWrittenWithTheirDefaultsAndChecked()
        {
            using var folder = new TempFolder();
            string path = folder.Combine("RegenerateFM", "settings.ini");
            var defaults = Settings.Load(path);
            Assert.True(File.Exists(path));
            Assert.Equal(("gpt-image-2.5-sunburst", "medium", "https://api.openai.com/v1"), (defaults.Model, defaults.Quality, defaults.ApiBaseUrl));
            var image = Settings.Load(path).ForKey("sk-test-0123456789abcdef");
            Assert.Equal(("https://api.openai.com/v1", "medium"), (image.BaseUrl, image.Quality));
            Assert.Equal("https://credits.regenerate.fm/v1", Settings.Load(path).ForKey("rfm_" + new string('t', 43)).BaseUrl);
            File.WriteAllText(path, File.ReadAllText(path).Replace("Quality = medium", "Quality = LOW")
                                                         .Replace("ApiBaseUrl = https://api.openai.com/v1", "ApiBaseUrl = http://127.0.0.1:8899/v1/"));
            image = Settings.Load(path).ForKey("sk-test-0123456789abcdef");
            Assert.Equal(("http://127.0.0.1:8899/v1", "low"), (image.BaseUrl, image.Quality));
            File.WriteAllText(path, "[OpenAI]\nApiBaseUrl = http://example.com/v1\n[Credits]\nServiceUrl = ftp://x\n");
            Assert.Contains("ApiBaseUrl in settings.ini must be an https address",
                            Assert.Throws<ArgumentException>(() => Settings.Load(path).ForKey("sk-test-0123456789abcdef")).Message);
            Assert.Contains("ServiceUrl", Assert.Throws<ArgumentException>(() => Settings.Load(path).CreditsServiceUrl).Message);
            Assert.Throws<MissingApiKeyException>(() => Settings.Load(folder.Combine("other.ini")).ForKey(null));
        }

        [Fact]
        public void ACreditsSignInWaitsUntilApprovedWithRegenerateFMsUserAgent()
        {
            using var credits = new StandIn();
            var signIn = Credits.Start(credits.Url).GetAwaiter().GetResult();
            Assert.Equal("WXYZ-K7P3", signIn.UserCode);
            Assert.StartsWith(credits.Url + "/link", signIn.Link);
            var answer = Credits.Wait(credits.Url, signIn).GetAwaiter().GetResult();
            Assert.Equal((credits.Token, 75, "player@example.com"), (answer.Token, answer.Credits, answer.Email));
            Assert.All(credits.Received, request => Assert.Equal("RegenerateFM", request.Agent));
            credits.Expire = true;
            Assert.Null(Credits.Wait(credits.Url, Credits.Start(credits.Url).GetAwaiter().GetResult()).GetAwaiter().GetResult());
            using var other = new StandIn { LinkHost = "https://example.com" };
            Assert.Contains("unexpected sign-in link", Assert.Throws<InvalidOperationException>(() =>
                Credits.Start(other.Url).GetAwaiter().GetResult()).Message);
            // Whole numbers written as 5.0 are taken; a missing or mistyped field is one message for the player.
            other.LinkHost = null;
            other.StartAnswer = answer => { answer["interval"] = 5.0; return answer; };
            Assert.Equal(5, Credits.Start(other.Url).GetAwaiter().GetResult().Interval);
            foreach (var change in new Action<Dictionary<string, object>>[]
                     {
                         answer => answer.Remove("user_code"), answer => answer["interval"] = "2", answer => answer["expires_in"] = 1e300,
                     })
            {
                other.StartAnswer = answer => { change(answer); return answer; };
                Assert.Equal("RegenerateFM credits sent an unexpected answer.", Assert.Throws<InvalidOperationException>(() =>
                    Credits.Start(other.Url).GetAwaiter().GetResult()).Message);
            }
            other.StartAnswer = answer => { answer["verification_uri_complete"] = null; return answer; };
            Assert.Contains("unexpected sign-in link", Assert.Throws<InvalidOperationException>(() =>
                Credits.Start(other.Url).GetAwaiter().GetResult()).Message);
            // A token's image requests go to the credits service.
            Assert.Null(ImageApi.CheckKey(new ImageSettings { BaseUrl = Credits.ApiBase(credits.Url) }, credits.Token).GetAwaiter().GetResult());
            Assert.Equal(ImageApi.SignedOut, ImageApi.CheckKey(new ImageSettings { BaseUrl = Credits.ApiBase(credits.Url) },
                                                               "rfm_" + new string('x', 43)).GetAwaiter().GetResult());
        }
    }
}
