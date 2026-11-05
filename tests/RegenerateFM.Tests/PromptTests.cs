using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace RegenerateFM.Tests
{
    // The prompts and appearance traits for a range of profiles, exactly as
    // Fixtures/expected-prompts.json has them.
    public class PromptTests
    {
        private static readonly JsonDocument Fixtures =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected-prompts.json")));

        public static ProfileSnapshot Profile(JsonElement json)
        {
            string Text(string name) =>
                json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            Dictionary<string, object> Stats(string name)
            {
                var stats = new Dictionary<string, object>();
                if (json.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Object)
                    foreach (var stat in values.EnumerateObject())
                        stats[stat.Name] = stat.Value.ValueKind == JsonValueKind.Number ? stat.Value.GetInt32() : stat.Value.GetString();
                return stats;
            }
            var profile = new ProfileSnapshot
            {
                Type = Text("type") ?? "player", Name = Text("name"), Nationality = Text("nationality"), JobTitle = Text("jobTitle"),
                Position = Text("position"), Club = Text("club"), Height = Text("height"), BiographyHeadline = Text("biographyHeadline"),
                Gender = Text("gender"), Ethnicity = Text("ethnicity"), SkinTone = Text("skinTone"), HairColour = Text("hairColour"),
                Likeness = json.TryGetProperty("likeness", out var likeness) && likeness.ValueKind == JsonValueKind.True,
                Age = json.TryGetProperty("age", out var age) ? age.GetInt32() : 0,
                Physical = Stats("physical"), Technical = Stats("technical"), Mental = Stats("mental"), Goalkeeping = Stats("goalkeeping"),
            };
            if (json.TryGetProperty("relatives", out var relatives))
                foreach (var relative in relatives.EnumerateArray())
                    profile.Relatives.Add((relative.GetProperty("relation").GetString(), relative.GetProperty("personId").GetString()));
            return profile;
        }

        private static string Optional(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        [Fact]
        public void PromptsMatchTheExpectedOnes()
        {
            int checkedCases = 0;
            foreach (var item in Fixtures.RootElement.GetProperty("cases").EnumerateArray())
            {
                string action = item.GetProperty("action").GetString();
                var profile = Profile(item.GetProperty("profile"));
                string personId = Optional(item, "personId");
                string prompt = null, error = null;
                try
                {
                    prompt = action switch
                    {
                        "update" => Prompts.UpdatePrompt(profile),
                        "newgen" => Prompts.NewgenPrompt(profile, personId, null),
                        "relative" => Prompts.RelativePrompt(profile, Optional(item, "relation"), personId, null),
                        _ => Prompts.ProfilePrompt(profile, personId, null),
                    };
                }
                catch (ArgumentException exception)
                {
                    error = exception.Message;
                }
                Assert.Equal(Optional(item, "error"), error);
                Assert.Equal(Optional(item, "prompt"), prompt);
                checkedCases++;
            }
            Assert.Equal(180, checkedCases);
        }

        [Fact]
        public void AppearanceTraitsMatchTheExpectedOnes()
        {
            foreach (var item in Fixtures.RootElement.GetProperty("traits").EnumerateArray())
            {
                var traits = Appearance.AppearanceTraits(item.GetProperty("seed").GetString(), item.GetProperty("age").GetInt32(),
                                                         Optional(item, "gender"), Optional(item, "family"));
                var expected = item.GetProperty("traits").EnumerateObject().ToDictionary(trait => trait.Name, trait => trait.Value.GetString());
                Assert.Equal(expected, traits);
            }
        }

        [Fact]
        public void SavedTraitsAreReusedAndChosenAgainForAKnownGender()
        {
            using var folder = new TempFolder();
            var player = new ProfileSnapshot
            {
                Name = "Jak Stewart", Age = 21, Nationality = "English", Position = "Goalkeeper", Club = "Example FC", Height = "190 cm",
                Technical = new Dictionary<string, object> { ["Passing"] = 12, ["First Touch"] = 7, ["Kicking"] = "11-14" },
                Mental = new Dictionary<string, object> { ["Decisions"] = 14, ["Bravery"] = 5, ["Composure"] = "12–12" },
            };
            string first = Prompts.ProfilePrompt(player, "111", folder.Path);
            Assert.True(File.Exists(Path.Combine(folder.Path, ".regeneratefm-appearances", "111.json")));
            Assert.Equal(first, Prompts.ProfilePrompt(player, "111", folder.Path));
            player.Gender = "female";
            Prompts.ProfilePrompt(player, "111", folder.Path);
            var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.Path, ".regeneratefm-appearances", "111.json")));
            Assert.Equal("female", saved.RootElement.GetProperty("gender").GetString());
            Assert.Throws<ArgumentException>(() => Prompts.ProfilePrompt(player, "x", folder.Path));
        }

        // Real players the model can't know, at one club and with the same FM
        // ethnicity, skin and hair, came out as one face: each now has their own
        // appearance traits and shade of FM's hair colour, saved like a newgen's.
        [Fact]
        public void RealTeamMatesWithTheSameLookInFMGetTheirOwnAppearance()
        {
            using var folder = new TempFolder();
            ProfileSnapshot Real(string name, int age) => new ProfileSnapshot
            {
                Name = name, Age = age, Nationality = "Scotland", Position = "Defender (Centre)", Club = "Edinburgh City",
                Gender = "male", Ethnicity = "NorthernEuropean", SkinTone = "White2", HairColour = "dark brown", Likeness = true,
            };
            var ids = new[] { "61097660", "61096166", "61089385", "61082163", "61043948", "2000412771", "2000115075" };
            var prompts = ids.Select(id => Prompts.ProfilePrompt(Real("Sam Smith", 23), id, folder.Path)).ToList();
            Assert.Equal(ids.Length, prompts.Distinct().Count());
            Assert.All(prompts, prompt =>
            {
                Assert.Contains("Make it look like Sam Smith as they look now. Ethnicity: white Northern European, with fair skin; " +
                                "follow this over any other appearance detail. Natural hair colour: ", prompt);
                Assert.Contains("Where you can't be sure how Sam Smith looks, use this individual appearance instead of a generic " +
                                "footballer's face. Individual appearance: ", prompt);
                Assert.Contains("avoid defaulting to a model-like face or the same tousled brown haircut", prompt);
            });
            // Saved, so the next face for the same player is described the same way.
            Assert.True(File.Exists(Path.Combine(folder.Path, ".regeneratefm-appearances", "61097660.json")));
            Assert.Equal(prompts[0], Prompts.ProfilePrompt(Real("Sam Smith", 23), ids[0], folder.Path));
            Assert.True(prompts.Select(prompt => Regex.Match(prompt, "Natural hair colour: ([^.]+)\\.").Groups[1].Value).Distinct().Count() > 1);
        }

        [Fact]
        public void FMsHairColourBecomesAShadeOfItForAWhiteNorthernEuropean()
        {
            var shades = Enumerable.Range(0, 40).Select(i => Appearance.HairShade("dark brown", "NorthernEuropean", i.ToString())).ToList();
            Assert.Equal(new[] { "chestnut brown", "dark brown", "medium brown", "mousy light brown", "very dark brown" },
                         shades.Distinct().OrderBy(shade => shade).ToArray());
            Assert.Equal(Appearance.HairShade("ginger", "NorthernEuropean", "7"), Appearance.HairShade("ginger", "NorthernEuropean", "7"));
            Assert.Equal("dark brown", Appearance.HairShade("dark brown", "AfricanCaribbean", "7"));
            Assert.Equal("black", Appearance.HairShade("black", "NorthernEuropean", "7"));
            // Relatives share a family's seed, and so a shade.
            Assert.Equal("family:12", Appearance.ShadeSeed("Sam Smith", "34", "family:12"));
            Assert.Equal("34", Appearance.ShadeSeed("Sam Smith", "34", null));
        }
    }

    public sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "regeneratefm-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
