using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RegenerateFM.Desktop.FM24
{
    // A profile as JSON, to log a preview and to compare two reads of one person.
    public static class ProfileJson
    {
        public static string Describe(ProfileSnapshot profile, bool withBiography = true)
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                json.WriteStartObject();
                void Text(string name, string value)
                {
                    if (value != null)
                        json.WriteString(name, value);
                }
                void Stats(string name, Dictionary<string, object> values)
                {
                    if (values == null || values.Count == 0)
                        return;
                    json.WriteStartObject(name);
                    foreach (var (stat, score) in values)
                    {
                        if (score is int number)
                            json.WriteNumber(stat, number);
                        else
                            json.WriteString(stat, score?.ToString());
                    }
                    json.WriteEndObject();
                }
                Text("type", profile.Type);
                Text("gender", profile.Gender);
                Text("name", profile.Name);
                json.WriteNumber("age", profile.Age);
                Text("nationality", profile.Nationality);
                Text("jobTitle", profile.JobTitle);
                Text("position", profile.Position);
                Text("club", profile.Club);
                Text("height", profile.Height);
                if (withBiography)
                    Text("biographyHeadline", profile.BiographyHeadline);
                Stats("physical", profile.Physical);
                Stats("technical", profile.Technical);
                Stats("mental", profile.Mental);
                Stats("goalkeeping", profile.Goalkeeping);
                json.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}
