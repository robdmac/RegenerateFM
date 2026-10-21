using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace RegenerateFMHotReload
{
    // Reads a JSON array of cases on stdin and writes one result per case.
    internal static class Program
    {
        private static async Task<int> Main()
        {
            using var input = JsonDocument.Parse(Console.In.ReadToEnd());
            var results = new List<Dictionary<string, object>>();
            foreach (var item in input.RootElement.EnumerateArray())
            {
                var result = new Dictionary<string, object>();
                try
                {
                    string action = item.GetProperty("action").GetString();
                    string dir = Optional(item, "directory");
                    string id = Optional(item, "personId");
                    if (action == "profile")
                        result["prompt"] = FaceGenerator.ProfilePrompt(Profile(item.GetProperty("profile")), id, dir);
                    else if (action == "update")
                        result["prompt"] = FaceGenerator.UpdatePrompt(Profile(item.GetProperty("profile")));
                    else if (action == "fit")
                        File.WriteAllBytes(Optional(item, "output"), FaceGenerator.FitPortrait(File.ReadAllBytes(Optional(item, "input"))));
                    else if (action == "save")
                        result["path"] = FaceGenerator.SavePortrait(dir, id, File.ReadAllBytes(Optional(item, "input")));
                    else if (action == "current")
                        result["found"] = FaceGenerator.CurrentPortrait(dir, id) != null;
                    else if (action == "savekey")
                        KeyStore.Save(Optional(item, "path"), Optional(item, "apiKey"));
                    else if (action == "loadkey")
                        result["apiKey"] = KeyStore.Load(Optional(item, "path"));
                    else if (action == "checkkey")
                        result["problem"] = await FaceGenerator.CheckKey(new ImageSettings { BaseUrl = Optional(item, "baseUrl") },
                                                                         Optional(item, "apiKey"));
                    else if (action == "portrait")
                    {
                        var settings = new ImageSettings { ApiKey = Optional(item, "apiKey"), BaseUrl = Optional(item, "baseUrl") };
                        string shown = Optional(item, "input");
                        result["path"] = await FaceGenerator.MakePortrait(settings, dir, id, Profile(item.GetProperty("profile")),
                                                                          Optional(item, "mode") == "update",
                                                                          shown != null ? File.ReadAllBytes(shown) : null);
                    }
                    else if (action == "generate" || action == "edit")
                    {
                        var settings = new ImageSettings
                        {
                            ApiKey = Optional(item, "apiKey"), Model = Optional(item, "model") ?? "gpt-image-2.5-sunburst",
                            Quality = Optional(item, "quality") ?? "medium", BaseUrl = Optional(item, "baseUrl")
                        };
                        var png = action == "generate"
                            ? await FaceGenerator.GeneratePng(settings, Optional(item, "prompt"))
                            : await FaceGenerator.EditPng(settings, Optional(item, "prompt"), File.ReadAllBytes(Optional(item, "input")));
                        File.WriteAllBytes(Optional(item, "output"), png);
                    }
                    else
                        throw new ArgumentException("unknown action " + action);
                }
                catch (Exception error)
                {
                    result["error"] = error.Message;
                }
                results.Add(result);
            }
            Console.Out.Write(JsonSerializer.Serialize(results));
            return 0;
        }

        private static string Optional(JsonElement item, string name)
        {
            return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static ProfileSnapshot Profile(JsonElement value)
        {
            var profile = new ProfileSnapshot
            {
                Type = Optional(value, "type") ?? "player",
                Name = Optional(value, "name"), Nationality = Optional(value, "nationality"),
                JobTitle = Optional(value, "jobTitle"), Position = Optional(value, "position"),
                Club = Optional(value, "club"), Height = Optional(value, "height"),
                BiographyHeadline = Optional(value, "biographyHeadline"), Gender = Optional(value, "gender"),
                Age = value.TryGetProperty("age", out var age) && age.ValueKind == JsonValueKind.Number ? age.GetInt32() : 0,
            };
            Stats(value, "physical", profile.Physical);
            Stats(value, "technical", profile.Technical);
            Stats(value, "mental", profile.Mental);
            Stats(value, "goalkeeping", profile.Goalkeeping);
            return profile;
        }

        private static void Stats(JsonElement value, string name, Dictionary<string, object> target)
        {
            if (!value.TryGetProperty(name, out var stats))
                return;
            foreach (var stat in stats.EnumerateObject())
                target[stat.Name] = stat.Value.ValueKind == JsonValueKind.Number ? stat.Value.GetInt32() : (object)stat.Value.GetString();
        }
    }
}
