using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RegenerateFM.Desktop.FM24.Setup
{
    // The optional RegenerateFM skin: FM24's own player and staff panels with
    // room below the portrait header, and RegenerateFM's portrait popups added
    // to them.
    public static class Skin
    {
        public const string SkinConfig =
            "<record>\n" +
            "  <translation id=\"name\" type=\"new\" value=\"RegenerateFM\"/>\n" +
            "  <string id=\"author\" value=\"RegenerateFM\"/>\n" +
            "  <translation id=\"description\" type=\"new\" value=\"FM24 on-demand portraits\"/>\n" +
            "  <string id=\"version\" value=\"0.1\"/>\n" +
            "  <flags id=\"parent\" value=\"fm\"/>\n" +
            "  <string id=\"skin_name\" value=\"RegenerateFM\"/>\n" +
            "  <string id=\"required_version\" value=\"24.0.0\"/>\n" +
            "</record>\n";
        public static readonly string[] PanelKinds = { "player", "staff" };

        private static XDocument ParseXml(string text, string what)
        {
            try
            {
                return XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException error)
            {
                throw new InvalidDataException(what + " is not valid XML: " + error.Message);
            }
        }

        // Text without a byte-order mark and with \n line endings, as Python's
        // read_text(encoding="utf-8-sig").
        public static string ReadText(string path)
        {
            string text = File.ReadAllText(path, new UTF8Encoding(false)).Replace("\r\n", "\n").Replace('\r', '\n');
            return text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
        }

        private static void WritePrivate(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // FM24's own player Overview with room below the portrait header.
        public static string AddPlayerPanel(string panelSource, string skinDir)
        {
            string original = ReadText(panelSource);
            ParseXml(original, "FM24's player overview panel");
            const string marker = "\t\t<!-- Middle row of large panels -->";
            if (Count(original, marker) != 1)
                throw new InvalidDataException("This does not look like FM24's vanilla player overview panel.");
            string modified = ReplaceFirst(original, marker, "\t\t<container height=\"32\" />\n\n" + marker);
            ParseXml(modified, "The RegenerateFM player panel");
            if (Directory.Exists(skinDir) && Directory.EnumerateFileSystemEntries(skinDir).Any())
                throw new IOException("Refusing to overwrite an existing skin: " + skinDir);
            string destination = Path.Combine(skinDir, "panels", "player", "player overview panel.xml");
            WritePrivate(destination, modified);
            File.WriteAllText(Path.Combine(skinDir, "skin_config.xml"), SkinConfig, new UTF8Encoding(false));
            return destination;
        }

        public static string AddStaffPanel(string panelSource, string skinDir)
        {
            string original = ReadText(panelSource);
            var root = ParseXml(original, "FM24's staff panel").Root;
            var layouts = root.Elements("layout").Where(child => (string)child.Attribute("class") == "arrange_vertical_attachment").ToList();
            if (layouts.Count != 1 || (string)layouts[0].Attribute("layout") != "95, -1")
                throw new InvalidDataException("This does not look like FM24's vanilla non-player profile panel.");
            if (root.Elements("container").Count() != 2)
                throw new InvalidDataException("Unexpected FM24 staff panel structure.");
            const string marker = "\t</container>\n\n\n\t<container>";
            if (Count(original, marker) != 1)
                throw new InvalidDataException("Could not locate the FM24 staff header boundary.");
            string modified = ReplaceFirst(original, "layout=\"95, -1\"", "layout=\"95, 32, -1\"");
            modified = ReplaceFirst(modified, marker, "\t</container>\n\n\t<container height=\"32\" />\n\n\t<container>");
            ParseXml(modified, "The RegenerateFM staff panel");
            string destination = Path.Combine(skinDir, "panels", "non_player", "non player profile.xml");
            WritePrivate(destination, modified);
            return destination;
        }

        private static int Count(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static string ReplaceFirst(string text, string part, string replacement)
        {
            int at = text.IndexOf(part, StringComparison.Ordinal);
            return at < 0 ? text : text.Substring(0, at) + replacement + text.Substring(at + part.Length);
        }

        // A transparent popup over the portrait. FM24 posts its select_event,
        // FMFG, for either row; the DLL reads the chosen row (FMFG or FMFU)
        // from the event.
        public static XElement Popup(string widgetId)
        {
            var items = new XElement("list", new XAttribute("id", "items"));
            foreach (var (id, text) in new[] { ("FMFG", "Generate new face"), ("FMFU", "Update face") })
                items.Add(new XElement("record", new XAttribute("id", id),
                                       new XElement("translation", new XAttribute("id", "text"), new XAttribute("type", "new"), new XAttribute("value", text))));
            return new XElement("widget",
                new XAttribute("class", "popup_button"), new XAttribute("id", widgetId), new XAttribute("select_event", "FMFG"),
                new XAttribute("fixed", "true"), new XAttribute("auto_size", "none"), new XAttribute("width", "88"),
                new XAttribute("height", "80"), new XAttribute("show_button_appearance", "false"),
                new XElement("layout", new XAttribute("class", "stick_to_sides_attachment"), new XAttribute("alignment", "left,vertical"),
                             new XAttribute("inset", "0")),
                new XElement("translation", new XAttribute("id", "hint"), new XAttribute("type", "new"), new XAttribute("value", "Choose a face action")),
                new XElement("string", new XAttribute("id", "text"), new XAttribute("value", "")),
                items);
        }

        // The panel with RegenerateFM's portrait popup added.
        public static string ModifyPanel(string source, string kind = "player")
        {
            if (Array.IndexOf(PanelKinds, kind) < 0)
                throw new ArgumentException("Unknown RegenerateFM panel kind: " + kind);
            var document = ParseXml(source, "The panel");
            var root = document.Root;
            if (root.DescendantsAndSelf().Any(node => (string)node.Attribute("id") == "fmnP" || (string)node.Attribute("id") == "fmnS"))
                throw new ArgumentException("This panel already has a native RegenerateFM popup.");
            if (kind == "staff")
            {
                var portraits = root.Descendants("widget").Where(node => (string)node.Attribute("id") == "ppic").ToList();
                if (portraits.Count != 1)
                    throw new ArgumentException("Could not identify the vanilla staff portrait widget.");
                portraits[0].Parent.Add(Popup("fmnS"));
            }
            else
            {
                var headers = root.Descendants("widget").Where(node => (string)node.Attribute("class") == "player_personal_details_panel" &&
                                                                       (string)node.Attribute("id") == "ppdp").ToList();
                if (headers.Count != 1)
                    throw new ArgumentException("Could not identify the vanilla player portrait header.");
                var header = headers[0];
                var wrapper = new XElement("container", new XAttribute("height", (string)header.Attribute("height") ?? "80"));
                header.Add(new XElement("layout", new XAttribute("class", "stick_to_sides_attachment"), new XAttribute("alignment", "all"),
                                        new XAttribute("inset", "0")));
                header.AddBeforeSelf(wrapper);
                header.Remove();
                wrapper.Add(header);
                wrapper.Add(Popup("fmnP"));
            }
            return Indented(root) + "\n";
        }

        // The element re-indented with tabs, without an XML declaration (as
        // Python's ElementTree.indent and tostring).
        private static string Indented(XElement root)
        {
            foreach (var text in root.DescendantNodesAndSelf().OfType<XText>().Where(node => string.IsNullOrWhiteSpace(node.Value)).ToList())
                text.Remove();
            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = true, Indent = true, IndentChars = "\t", NewLineChars = "\n", Encoding = new UTF8Encoding(false),
            };
            var output = new StringBuilder();
            using (var writer = XmlWriter.Create(output, settings))
                root.Save(writer);
            return output.ToString();
        }
    }
}
