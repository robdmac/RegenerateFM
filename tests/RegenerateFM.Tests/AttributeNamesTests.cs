using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace RegenerateFM.Tests
{
    // A generated player's (a newgen's) profile in French and in German, read
    // row by row as the plugin reads FM's Overview (AttributeNames.Add, with
    // FM27's own text for the IDs, from Fixtures/attribute-texts.json), then
    // sent through the newgen prompt, which lists the attributes.
    public class AttributeNamesTests
    {
        private static readonly JsonDocument Texts =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "attribute-texts.json")));

        private static Dictionary<string, string> English(string language)
        {
            var texts = Texts.RootElement.GetProperty(language).EnumerateObject()
                             .ToDictionary(text => int.Parse(text.Name), text => text.Value.GetString());
            return AttributeNames.English(id => texts.TryGetValue(id, out string text) ? text : null);
        }

        // The rows as the profile shows them: the column's heading, the name, the score.
        private static ProfileSnapshot Read(ProfileSnapshot profile, string language, params (string Heading, string Shown, object Score)[] rows)
        {
            var english = English(language);
            foreach (var (heading, shown, score) in rows)
                AttributeNames.Add(profile, english, shown, score, () => heading);
            return profile;
        }

        private static IEnumerable<string> AllNames(ProfileSnapshot profile) =>
            profile.Physical.Keys.Concat(profile.Technical.Keys).Concat(profile.Mental.Keys).Concat(profile.Goalkeeping.Keys);

        private static readonly HashSet<string> EnglishNames = new HashSet<string>(AttributeNames.TextIds.Select(name => name.English));

        // Wes Houghton (Leicester U18), the newgen whose face failed with
        // "Unknown physical stats: Accélération, ..." in 1.0.3: his Overview as
        // the player's French FM27 showed it.
        [Fact]
        public void AFrenchNewgenGoalkeeperIsDescribedInEnglish()
        {
            var profile = Read(new ProfileSnapshot
            {
                Name = "Wes Houghton", Age = 15, Nationality = "Angleterre", Position = "Gardien de but",
                Club = "Leicester U18", Height = "186 cm", Likeness = false,
            }, "french",
                ("Goalkeeping", "Communication", 7), ("Goalkeeping", "Contrôle de balle", 3),
                ("Goalkeeping", "Dégagement au poing (tendance)", 9), ("Goalkeeping", "Dégagements", 6),
                ("Goalkeeping", "Détente", 9), ("Goalkeeping", "Excentricité", 10), ("Goalkeeping", "Passes", 7),
                ("Goalkeeping", "Prises de balle", 11), ("Goalkeeping", "Réflexes", 11), ("Goalkeeping", "Relances à la main", 9),
                ("Goalkeeping", "Sorties dans la surface", 12), ("Goalkeeping", "Sorties dans les pieds (tendance)", 13),
                ("Goalkeeping", "Un contre un", 3),
                ("Mental", "Agressivité", 13), ("Mental", "Anticipation", 7), ("Mental", "Appels de balle", 2),
                ("Mental", "Concentration", 5), ("Mental", "Courage", 8), ("Mental", "Décisions", 12), ("Mental", "Détermination", 17),
                ("Mental", "Inspiration", 2), ("Mental", "Jeu collectif", 3), ("Mental", "Leadership", 11), ("Mental", "Placement", 6),
                ("Mental", "Sang-froid", 7), ("Mental", "Vision du jeu", 8), ("Mental", "Volume de jeu", 5),
                ("Physical", "Accélération", 4), ("Physical", "Agilité", 14), ("Physical", "Détente verticale", 13),
                ("Physical", "Endurance", 7), ("Physical", "Équilibre", 12), ("Physical", "Puissance", 4),
                ("Physical", "Qualités phys. nat.", 12), ("Physical", "Vitesse", 5),
                ("Technical", "Coups francs", 5), ("Technical", "Penalty", 2), ("Technical", "Technique", 7));

            Assert.Equal(new[] { "Acceleration", "Agility", "Balance", "Jumping Reach", "Natural Fitness", "Pace", "Stamina", "Strength" },
                         profile.Physical.Keys.OrderBy(name => name, StringComparer.Ordinal));
            Assert.All(AllNames(profile), name => Assert.Contains(name, EnglishNames));
            Assert.Equal(13, profile.Goalkeeping["Rushing Out (Tendency)"]);
            Assert.Equal(9, profile.Goalkeeping["Punching (Tendency)"]);
            Assert.Equal(9, profile.Goalkeeping["Aerial Reach"]);
            Assert.Equal(5, profile.Technical["Free Kick Taking"]);
            // FM27 lists First Touch and Passing under a goalkeeper's Goalkeeping heading, as in English.
            Assert.Equal(3, profile.Goalkeeping["First Touch"]);

            string prompt = Prompts.ProfilePrompt(profile, "2002108127", null);
            Assert.Contains("Physical stats: Acceleration 4/20, Agility 14/20, Balance 12/20, Jumping Reach 13/20, " +
                            "Natural Fitness 12/20, Pace 5/20, Stamina 7/20, Strength 4/20. ", prompt);
            Assert.Contains("Best non-physical stats: mental Determination 17/20, mental Aggression 13/20, " +
                            "goalkeeping Rushing Out (Tendency) 13/20. ", prompt);
            Assert.Contains("Weakest non-physical stats: mental Flair 2/20, mental Off The Ball 2/20, technical Penalty Taking 2/20. ", prompt);
            // Strength 4 shapes the face, as it does in English.
            Assert.Contains("a slender neck and slight build", prompt);
        }

        // An outfield newgen in German, with each section's names.
        [Fact]
        public void AGermanNewgenIsDescribedInEnglish()
        {
            var profile = Read(new ProfileSnapshot
            {
                Name = "Jonas Becker", Age = 16, Nationality = "Deutschland", Position = "Stürmer (Mitte)",
                Club = "Leicester U18", Height = "181 cm", Likeness = false,
            }, "german",
                ("Technical", "Ecken", 6), ("Technical", "Flanken", 8), ("Technical", "Dribbling", 12), ("Technical", "Abschluss", 14),
                ("Technical", "Ballannahme", 11), ("Technical", "Freistöße", 5), ("Technical", "Kopfballtechnik", 9),
                ("Technical", "Weitschüsse", 10), ("Technical", "Weite Einwürfe", 3), ("Technical", "Deckung", 4),
                ("Technical", "Passen", 9), ("Technical", "Elfmeter", 11), ("Technical", "Tackling", 5), ("Technical", "Technik", 12),
                ("Mental", "Aggressivität", 9), ("Mental", "Antizipation", 11), ("Mental", "Mut", 10), ("Mental", "Nervenstärke", 12),
                ("Mental", "Konzentration", 8), ("Mental", "Entscheidungen", 9), ("Mental", "Zielstrebigkeit", 15),
                ("Mental", "Kreativität", 13), ("Mental", "Führungsqualitäten", 4), ("Mental", "Ohne Ball", 14),
                ("Mental", "Stellungsspiel", 3), ("Mental", "Teamwork", 7), ("Mental", "Übersicht", 10), ("Mental", "Einsatzfreude", 11),
                ("Physical", "Antritt", 15), ("Physical", "Beweglichkeit", 13), ("Physical", "Balance", 10), ("Physical", "Sprunghöhe", 8),
                ("Physical", "Grundfitness", 12), ("Physical", "Schnelligkeit", 16), ("Physical", "Ausdauer", 11), ("Physical", "Kraft", 17));

            Assert.Equal(8, profile.Physical.Count);
            Assert.Equal(14, profile.Technical.Count);
            Assert.Equal(14, profile.Mental.Count);
            Assert.All(AllNames(profile), name => Assert.Contains(name, EnglishNames));
            Assert.Equal(13, profile.Mental["Flair"]);
            Assert.Equal(15, profile.Mental["Determination"]);

            string prompt = Prompts.ProfilePrompt(profile, "2002108200", null);
            Assert.Contains("Physical stats: Acceleration 15/20, Agility 13/20, Balance 10/20, Jumping Reach 8/20, " +
                            "Natural Fitness 12/20, Pace 16/20, Stamina 11/20, Strength 17/20. ", prompt);
            Assert.Contains("Best non-physical stats: mental Determination 15/20, technical Finishing 14/20, mental Off The Ball 14/20. ", prompt);
            Assert.Contains("a thick, muscular neck", prompt);
        }

        [Fact]
        public void EnglishNamesStayAsTheyAre()
        {
            var english = English("french");
            foreach (var (name, _) in AttributeNames.TextIds)
                Assert.Equal(name, english[name]);
        }

        // If FM's IDs moved, a name read as physical under another heading (or
        // the other way round) is kept as shown, under its heading.
        [Fact]
        public void ANameItsHeadingDisagreesWithIsKeptAsShown()
        {
            var warnings = new List<string>();
            var profile = new ProfileSnapshot();
            var english = English("french");
            AttributeNames.Add(profile, english, "Vitesse", 12, () => "Mental", warnings.Add);
            AttributeNames.Add(profile, english, "Courage", 9, () => "Physical", warnings.Add);
            AttributeNames.Add(profile, english, "Inconnu", 7, () => "Physical", warnings.Add);
            Assert.Equal(12, profile.Mental["Vitesse"]);
            Assert.Equal(9, profile.Physical["Courage"]);
            Assert.Equal(7, profile.Physical["Inconnu"]);
            Assert.Equal(2, warnings.Count);
            // And the prompt still describes a physical name it doesn't know.
            Assert.Contains("Courage 9/20", Prompts.ProfilePrompt(Read(new ProfileSnapshot
            {
                Name = "Wes Houghton", Age = 15, Nationality = "Angleterre", Position = "Gardien de but",
                Club = "Leicester U18", Height = "186 cm", Physical = profile.Physical,
            }, "french", ("Mental", "Décisions", 12), ("Mental", "Courage", 8), ("Mental", "Leadership", 11),
                         ("Goalkeeping", "Réflexes", 11), ("Goalkeeping", "Prises de balle", 11), ("Goalkeeping", "Un contre un", 3)),
                "2002108127", null));
        }

        // No two attributes share a text in French or German, so none is left out.
        [Fact]
        public void EveryNameIsReadInFrenchAndGerman()
        {
            foreach (string language in new[] { "french", "german" })
            {
                var english = English(language);
                var texts = Texts.RootElement.GetProperty(language);
                foreach (var (name, ids) in AttributeNames.TextIds)
                    foreach (int id in ids)
                        Assert.Equal(name, english[texts.GetProperty(id.ToString()).GetString()]);
            }
        }
    }
}
