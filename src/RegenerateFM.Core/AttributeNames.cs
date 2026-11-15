using System;
using System.Collections.Generic;
using System.Linq;

namespace RegenerateFM
{
    // The attributes a profile shows (FM26 and FM27's Overview), by their
    // English names: the prompt and the face's appearance use these. In
    // another language a name is put into English by FM's text for its IDs
    // (the plugin's Profiles.cs reads the rows; tests/AttributeNamesTests).
    public static class AttributeNames
    {
        public static readonly IReadOnlyCollection<string> Physical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Acceleration", "Agility", "Balance", "Jumping Reach", "Natural Fitness", "Pace", "Stamina", "Strength"
        };
        public static readonly IReadOnlyCollection<string> Mental = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Aggression", "Anticipation", "Bravery", "Composure", "Concentration", "Decisions", "Determination",
            "Flair", "Leadership", "Off The Ball", "Positioning", "Teamwork", "Vision", "Work Rate"
        };
        public static readonly IReadOnlyCollection<string> Goalkeeping = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Aerial Reach", "Command Of Area", "Communication", "Eccentricity", "Handling", "Kicking", "One On Ones",
            "Punching (Tendency)", "Reflexes", "Rushing Out (Tendency)", "Throwing"
        };

        // The IDs of FM's text for each name: its attribute list (9463-9512),
        // the "(Tendency)" and "Free Kick Taking" names that list lacks, and
        // FM27's second list (41368-41414, which FM26 doesn't have). Read with
        // the plugin's regeneratefm-dump-texts flag (UiDump.cs) in FM27 27.0.1
        // in English, French and German, and in FM26 26.3.2 in English. The
        // profile's labels are bound to the attribute's name, not to an ID, so
        // they are matched by FM's text for these IDs in the player's language.
        public static readonly IReadOnlyList<(string English, int[] Ids)> TextIds = new[]
        {
            ("Acceleration", new[] { 9505, 41368 }), ("Agility", new[] { 9506, 41399 }), ("Balance", new[] { 9507, 41400 }),
            ("Jumping Reach", new[] { 9508, 41401 }), ("Natural Fitness", new[] { 9509, 41394 }), ("Pace", new[] { 9510, 41378 }),
            ("Stamina", new[] { 9511, 41379 }), ("Strength", new[] { 9512, 41381 }),
            ("Aggression", new[] { 9489, 41369 }), ("Anticipation", new[] { 9490, 41396 }), ("Bravery", new[] { 9491, 41406 }),
            ("Composure", new[] { 9492, 41398 }), ("Concentration", new[] { 9493, 41370 }), ("Decisions", new[] { 9494, 41371 }),
            ("Determination", new[] { 9495, 41372 }), ("Flair", new[] { 9496, 41373 }), ("Leadership", new[] { 9497, 41408 }),
            ("Off The Ball", new[] { 9498, 41374 }), ("Positioning", new[] { 9499, 41375 }), ("Teamwork", new[] { 9500 }),
            ("Vision", new[] { 9502, 41376 }), ("Work Rate", new[] { 9504, 41377 }),
            ("Aerial Reach", new[] { 9477 }), ("Command Of Area", new[] { 9478, 41411 }), ("Communication", new[] { 9479, 41391 }),
            ("Eccentricity", new[] { 9480, 41412 }), ("Handling", new[] { 9481, 41413 }), ("Kicking", new[] { 9482, 41414 }),
            ("One On Ones", new[] { 9484, 41407 }), ("Punching (Tendency)", new[] { 13568 }), ("Reflexes", new[] { 9486, 41397 }),
            ("Rushing Out (Tendency)", new[] { 24875, 41393 }), ("Throwing", new[] { 9488, 41380 }),
            ("Corners", new[] { 9463, 41402 }), ("Crossing", new[] { 9464, 41403 }), ("Dribbling", new[] { 9465, 41404 }),
            ("Finishing", new[] { 9466, 41405 }), ("First Touch", new[] { 9467, 41392 }), ("Free Kick Taking", new[] { 24858, 41382 }),
            ("Heading", new[] { 9469, 41383 }), ("Long Shots", new[] { 9470, 41384 }), ("Long Throws", new[] { 9471, 41385 }),
            ("Marking", new[] { 9472, 41386 }), ("Passing", new[] { 9473, 41387 }), ("Penalty Taking", new[] { 9474, 41388 }),
            ("Tackling", new[] { 9475, 41389 }), ("Technique", new[] { 9476, 41390 }),
        };

        // The names in the player's language (text gives FM's text for an ID,
        // or null), and the English ones themselves, to the English names. A
        // text two attributes share is left out.
        public static Dictionary<string, string> English(Func<int, string> text)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (english, ids) in TextIds)
                foreach (string shown in ids.Select(text).Append(english))
                {
                    string key = shown?.Trim();
                    if (string.IsNullOrEmpty(key))
                        continue;
                    if (names.TryGetValue(key, out string other) && other != english)
                        shared.Add(key);
                    else
                        names[key] = english;
                }
            foreach (string key in shared)
                names.Remove(key);
            return names;
        }

        // Adds an attribute row to the profile: its name as shown, its score
        // (1-20, or a scouted range), and the section of its column's heading
        // ("Physical", "Mental", "Goalkeeping", "Technical" or null), asked for
        // only when it is needed. The English name is used unless the heading
        // disagrees about it being physical (FM's text IDs moved), when the
        // name is kept as shown, under its heading; warn is told.
        public static void Add(ProfileSnapshot profile, IReadOnlyDictionary<string, string> english, string shown, object score,
                               Func<string> section, Action<string> warn = null)
        {
            string stat = shown.Trim();
            string heading = null;
            bool headingRead = false;
            string Heading()
            {
                if (!headingRead)
                {
                    heading = section?.Invoke();
                    headingRead = true;
                }
                return heading;
            }
            if (english.TryGetValue(stat, out string name) && name != stat)
            {
                if (Heading() == null || (Heading() == "Physical") == Physical.Contains(name))
                    stat = name;
                else
                    warn?.Invoke("\"" + stat + "\" reads as " + name + ", but is under the " + Heading() + " heading; kept as shown");
            }
            if (Physical.Contains(stat))
                profile.Physical[stat] = score;
            else if (Mental.Contains(stat))
                profile.Mental[stat] = score;
            else if (Goalkeeping.Contains(stat))
                profile.Goalkeeping[stat] = score;
            else
                // A name not known in English goes under its column's heading.
                switch (Heading())
                {
                    case "Physical": profile.Physical[stat] = score; break;
                    case "Mental": profile.Mental[stat] = score; break;
                    case "Goalkeeping": profile.Goalkeeping[stat] = score; break;
                    default: profile.Technical[stat] = score; break;
                }
        }
    }
}
