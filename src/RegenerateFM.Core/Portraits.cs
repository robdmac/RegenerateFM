using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace RegenerateFM
{
    // The faces folder: RegenerateFM's portraits and the config.xml that maps
    // them to people.
    public static class Portraits
    {
        internal static readonly Regex IdPattern = new Regex(@"^[0-9]{1,20}$", RegexOptions.Compiled);

        internal static readonly object SaveLock = new object();

        // One in-game request: build the prompt, ask the image API and save the
        // face. Update face prefers RegenerateFM's own saved file to the game's
        // on-screen copy; ``shownNewgen`` says that copy is FM26's 3D newgen
        // face, which becomes a photograph. Nothing is saved when the request fails.
        public static async Task<string> MakePortrait(ImageSettings settings, string folder, string personId,
                                                      ProfileSnapshot profile, bool update, byte[] shownPng,
                                                      CancellationToken cancel = default, bool shownNewgen = false)
        {
            ValidateId(personId);
            CheckGraphicsConfig(folder);
            byte[] png;
            if (update)
            {
                var saved = CurrentPortrait(folder, personId);
                var reference = saved ?? shownPng;
                if (reference == null)
                    throw new InvalidOperationException(Text.T("This person has no face to update yet. Use Generate face instead."));
                string prompt = saved == null && shownNewgen ? Prompts.NewgenPrompt(profile, personId, folder) : Prompts.UpdatePrompt(profile, personId);
                png = await ImageApi.EditPng(settings, prompt, reference, cancel).ConfigureAwait(false);
            }
            else
            {
                // A relative's RegenerateFM face, if one has one, lends a slight
                // family resemblance (closest relative first); a real person's
                // likeness needs none.
                byte[] relativeFace = null;
                string relation = null;
                foreach (var relative in profile.Likeness ? Enumerable.Empty<(string Relation, string PersonId)>() : Prompts.FamilyRelatives(profile))
                    if ((relativeFace = CurrentPortrait(folder, relative.PersonId)) != null)
                    {
                        relation = relative.Relation;
                        break;
                    }
                png = relativeFace != null
                    ? await ImageApi.EditPng(settings, Prompts.RelativePrompt(profile, relation, personId, folder), relativeFace, cancel).ConfigureAwait(false)
                    : await ImageApi.GeneratePng(settings, Prompts.ProfilePrompt(profile, personId, folder), cancel).ConfigureAwait(false);
            }
            return SavePortrait(folder, personId, png, owner: profile.Name);
        }

        // ---- Saving into FM26's graphics folder ----------------------------

        public static string ValidateId(string value)
        {
            if (value == null || !IdPattern.IsMatch(value))
                throw new ArgumentException("Player ID must contain only digits (up to 20).");
            return value;
        }

        private static HashSet<string> Targets(string personId) =>
            new HashSet<string> { "graphics/pictures/person/" + personId + "/portrait",
                                  "graphics/pictures/person/r-" + personId + "/portrait" };

        // A file RegenerateFM saved for this person: "<id>" (before faces were
        // versioned, and from the FM26 web page) or "<id>-<16 hex>".
        internal static bool IsPortraitStem(string stem, string personId) =>
            Regex.IsMatch(stem ?? "", "^" + personId + "-[0-9a-f]{16}$");

        // RegenerateFM's saved portrait for this ID, if config.xml still maps it.
        public static byte[] CurrentPortrait(string directory, string personId)
        {
            string path = CurrentFile(directory, personId);
            if (path == null)
                return null;
            var png = File.ReadAllBytes(path);
            return Png.IsPng(png) && png.Length <= ImageApi.MaxReference ? png : null;
        }

        // The file of RegenerateFM's face that config.xml maps for this ID, or null.
        public static string CurrentFile(string directory, string personId)
        {
            ValidateId(personId);
            var (_, maps) = LoadMaps(directory);
            var targets = Targets(personId);
            foreach (var record in maps.Elements("record"))
            {
                string stem = (string)record.Attribute("from");
                if (!IsPortraitStem(stem, personId) || !targets.Contains((string)record.Attribute("to")))
                    continue;
                string path = Path.Combine(directory, stem + ".png");
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        public static void CheckGraphicsConfig(string directory)
        {
            LoadMaps(directory);
        }

        private static (XDocument Document, XElement Maps) LoadMaps(string directory)
        {
            string path = Path.Combine(directory, "config.xml");
            if (!File.Exists(path))
            {
                var maps = new XElement("list", new XAttribute("id", "maps"));
                return (new XDocument(new XElement("record", maps)), maps);
            }
            XDocument document;
            try
            {
                document = XDocument.Load(path);
            }
            catch (XmlException error)
            {
                throw new ArgumentException("config.xml in the RegenerateFM graphics folder could not be read: " + error.Message);
            }
            var found = document.Root?.Elements("list").FirstOrDefault(list => (string)list.Attribute("id") == "maps");
            if (found == null)
                throw new ArgumentException("Existing config.xml has no maps list; refusing to alter it.");
            return (document, found);
        }

        // Saves the portrait under a new name, "<id>-<16 hex>.png", and maps it
        // to the person's ordinary and generated-player IDs, as the FM24
        // companion does: FM26 keeps a staff portrait it has drawn and doesn't
        // load the same file again, so a face saved over the old file would
        // not appear until a restart. The person's earlier files go into the
        // history (FaceHistory) once config.xml maps the new one; if config.xml
        // fails, the new file is removed. ``removeEarlier`` false keeps the
        // person's earlier files for FM24, which shows them until its skin
        // reload succeeds (RemoveEarlier then). ``owner`` is the name shown
        // with the face, ``made`` when it was first made.
        public static string SavePortrait(string directory, string personId, byte[] png, bool removeEarlier = true,
                                          string owner = null, DateTime? made = null)
        {
            ValidateId(personId);
            if (!Png.IsPng(png))
                throw new ArgumentException("Portrait must be a PNG image.");
            lock (SaveLock)
            {
                Directory.CreateDirectory(directory);
                string stem = personId + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
                string imagePath = Path.Combine(directory, stem + ".png");
                string configPath = Path.Combine(directory, "config.xml");
                var (document, maps) = LoadMaps(directory);
                var targets = Targets(personId);
                foreach (var record in maps.Elements("record").Where(record => targets.Contains((string)record.Attribute("to"))).ToList())
                    record.Remove();
                foreach (var prefix in new[] { "", "r-" })
                    maps.Add(new XElement("record", new XAttribute("from", stem),
                                          new XAttribute("to", "graphics/pictures/person/" + prefix + personId + "/portrait")));
                byte[] xml = ConfigBytes(document);
                AtomicFile.Write(imagePath, png);
                try
                {
                    AtomicFile.Write(configPath, xml);
                }
                catch (Exception)
                {
                    try
                    {
                        File.Delete(imagePath);
                    }
                    catch (Exception rollback)
                    {
                        throw new InvalidOperationException("Could not save config.xml or remove the new portrait: " + rollback.Message);
                    }
                    throw;
                }
                FaceHistory.Added(directory, personId, stem, owner, made ?? DateTime.UtcNow);
                if (removeEarlier)
                    RemoveEarlier(imagePath, personId);
                return imagePath;
            }
        }

        private static byte[] ConfigBytes(XDocument document)
        {
            using var buffer = new MemoryStream();
            using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false) }))
                document.Save(writer);
            return buffer.ToArray();
        }

        // Takes the person's face out of config.xml; FaceHistory keeps the file.
        internal static void Unmap(string directory, string personId)
        {
            lock (SaveLock)
            {
                var (document, maps) = LoadMaps(directory);
                var targets = Targets(personId);
                foreach (var record in maps.Elements("record").Where(record => targets.Contains((string)record.Attribute("to"))).ToList())
                    record.Remove();
                AtomicFile.Write(Path.Combine(directory, "config.xml"), ConfigBytes(document));
            }
        }

        // The person's files beside ``current`` that config.xml no longer maps
        // go into the history; one that can't be moved now goes after the next face.
        public static void RemoveEarlier(string current, string personId)
        {
            lock (SaveLock)
                FaceHistory.Retire(Path.GetDirectoryName(current), personId, Path.GetFileNameWithoutExtension(current));
        }
    }
}
