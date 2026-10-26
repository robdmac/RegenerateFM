using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RegenerateFM.Desktop.FM24.Memory
{
    // FM24's UI widgets: each knows its manager, parent, skin ID ('head',
    // 'subs', …) and children vector.
    public static class Widgets
    {
        public const int Manager = 0x48, Parent = 0x50, Id = 0x5C, Children = 0x100;

        public static int SkinId(string name) => BinaryPrimitives.ReadInt32BigEndian(Encoding.ASCII.GetBytes(name));

        // The UI manager FM24's own reload command uses (top of its screen stack).
        public static long UiManager(GameProcess game)
        {
            long stack = game.U64(game.U64(game.ImageBase + game.Layout.ManagerGlobal) + 0x68);
            long begin = game.U64(stack + 0x18), end = game.U64(stack + 0x20);
            if (begin == 0 || end - begin < 8)
                throw new MemoryUnavailableException("FM24 has no active UI manager.");
            return game.U64(begin);
        }

        public static List<long> ChildrenOf(GameProcess game, long widget)
        {
            long begin = game.U64(widget + Children), end = game.U64(widget + Children + 8);
            if (end < begin || (end - begin) % 8 != 0 || end - begin > 8 * 8192)
                throw new MemoryUnavailableException("Invalid FM24 widget children.");
            // Reads are at most 4 KB (512 children); Lionel Messi's profile has a widget with more.
            var children = new List<long>();
            for (long start = 0; start < end - begin; start += GameProcess.MaxRead)
            {
                var data = game.Read(begin + start, (int)Math.Min(GameProcess.MaxRead, end - begin - start));
                for (int i = 0; i < data.Length; i += 8)
                    children.Add(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(i)));
            }
            return children;
        }

        // True when the widget is a child of its parent, up to a parentless root.
        public static bool Attached(GameProcess game, long widget, long manager)
        {
            for (int i = 0; i < 64; i++)
            {
                if (game.U64(widget + Manager) != manager)
                    return false;
                long parent = game.U64(widget + Parent);
                if (parent == 0)
                    return true;
                if (!ChildrenOf(game, parent).Contains(widget))
                    return false;
                widget = parent;
            }
            return false;
        }
    }

    public sealed class OpenScreen : IEquatable<OpenScreen>
    {
        public string PersonId;
        public string Biography = "";                    // first paragraph shown, or "" when none is shown
        public string Title = "";                        // the header's "Role - Club" text, without its ID
        public long Record;                              // the person's record, when FM24's person table gave the person
        public HashSet<int> Ages = new HashSet<int>();   // the "<n> years old" texts shown
        public DateTime? Today;                          // the in-game date in the top bar

        public bool Equals(OpenScreen other) =>
            other != null && PersonId == other.PersonId && Biography == other.Biography && Title == other.Title &&
            Record == other.Record && Ages.SetEquals(other.Ages) && Today == other.Today;

        public override bool Equals(object other) => Equals(other as OpenScreen);
        public override int GetHashCode() => HashCode.Combine(PersonId, Title, Record);
    }

    // The open FM24 screen's header and biography, from its widget tree. The UI
    // manager's root widget (manager+0x58) is the open screen. Its tree is
    // walked through each widget's children vector, keeping only widgets of
    // this manager. The header ('head') holds the subtitle ('subs') whose text
    // ends in "(ID:n)" when screen IDs are enabled; without them, the person
    // comes from the screen's object browser (PersonHandles). The biography
    // panel's 'biot' widget holds the rich-text biography FM24 is displaying, a
    // text widget the person's "<n> years old", and the top bar's 'dtam'
    // widgets the in-game date. Anything unexpected raises
    // MemoryUnavailableException.
    public static class ScreenReader
    {
        public const int ScreenRoot = 0x58;
        public const int SubtitleText = 0x190;   // widget part +0x158: {u32 length, UTF-8}
        public const int BiographyText = 0x188;  // 'biot' widget: {u32 length, rich text}
        public const int WidgetText = 0x188;     // a text widget's {u32 length, UTF-8}, as the biography's
        private const int MaxWidgets = 20000;
        private static readonly Regex IdSuffix = new Regex(@"\(ID:([1-9][0-9]{0,9})\)$", RegexOptions.Compiled);
        private static readonly Regex Age = new Regex(@"^([1-9][0-9]?) years old\z", RegexOptions.Compiled);
        private static readonly Regex ShownDate = new Regex(@"^([1-9]|[12][0-9]|3[01]) (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) ((?:19|20)[0-9]{2})\z", RegexOptions.Compiled);
        public static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        // A biography's links: \x01, base64, \x02, around the shown text.
        private static readonly Regex RichLink = new Regex("\x01[A-Za-z0-9+/=]{12,500}\x02", RegexOptions.Compiled);

        // Widgets under ``root`` by skin ID, bounded and without cycles.
        public static Dictionary<int, List<long>> Tree(GameProcess game, long manager, long root)
        {
            var found = new Dictionary<int, List<long>>();
            var seen = new HashSet<long>();
            var pending = new Stack<long>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                long widget = pending.Pop();
                if (!seen.Add(widget))
                    continue;
                if (seen.Count > MaxWidgets)
                    throw new MemoryUnavailableException("The FM24 screen has too many widgets to read.");
                int skinId;
                try
                {
                    if (game.U64(widget + Widgets.Manager) != manager)
                        continue;
                    skinId = (int)game.U32(widget + Widgets.Id);
                }
                catch (Exception error) when (People.Unreadable(error))
                {
                    continue;  // Not a widget: a leaf's data looked like a children vector.
                }
                if (!found.TryGetValue(skinId, out var list))
                    found[skinId] = list = new List<long>();
                list.Add(widget);
                try
                {
                    // Depth first, last child first.
                    foreach (long child in Widgets.ChildrenOf(game, widget))
                        pending.Push(child);
                }
                catch (Exception error) when (error is MemoryUnavailableException || People.Unreadable(error))
                {
                    // Not a container: other data where the children vector would be.
                }
            }
            return found;
        }

        private static List<long> Named(Dictionary<int, List<long>> widgets, string name) =>
            widgets.TryGetValue(Widgets.SkinId(name), out var found) ? found : new List<long>();

        private static byte[] WidgetString(GameProcess game, long widget, int offset, int limit)
        {
            long text = game.U64(widget + offset);
            if (text == 0)
                return Array.Empty<byte>();
            uint size = game.U32(text);
            if (size > 1 << 20)
                throw new MemoryUnavailableException("Invalid FM24 widget text.");
            int length = (int)Math.Min(size, (uint)limit);
            var data = new byte[length];
            for (int start = 0; start < length; start += GameProcess.MaxRead)
                game.Read(text + 4 + start, Math.Min(GameProcess.MaxRead, length - start)).CopyTo(data, start);
            return data;
        }

        // The first paragraph of a rich-text biography, links reduced to their text.
        public static string FirstParagraph(byte[] raw)
        {
            int end = raw.Length;
            for (int i = 0; i + 1 < raw.Length; i++)
                if (raw[i] == '\n' && raw[i + 1] == '\n')
                {
                    end = i;
                    break;
                }
            int nul = Array.IndexOf(raw, (byte)0, 0, end);
            if (nul >= 0)
                end = nul;
            string text;
            try
            {
                string latin = RichLink.Replace(Encoding.Latin1.GetString(raw, 0, end), "").Replace("\x01", "");
                text = People.Utf8.GetString(Encoding.Latin1.GetBytes(latin));
            }
            catch (DecoderFallbackException)
            {
                throw new MemoryUnavailableException("Invalid FM24 biography text.");
            }
            text = People.CollapseSpaces(text);
            if (text.Length > 2000 || !People.IsPrintable(text))
                throw new MemoryUnavailableException("Invalid FM24 biography text.");
            return text;
        }

        // The person ID in the open screen's header, and the biography shown.
        public static OpenScreen Read(GameProcess game)
        {
            try
            {
                long manager = Widgets.UiManager(game);
                var widgets = Tree(game, manager, game.U64(manager + ScreenRoot));
                var heads = Named(widgets, "head");
                if (heads.Count != 1)
                    throw new MemoryUnavailableException("Expected one FM24 profile header; found " + heads.Count + ".");
                var subtitles = Named(Tree(game, manager, heads[0]), "subs");
                if (subtitles.Count != 1)
                    throw new MemoryUnavailableException("Could not find the FM24 profile subtitle.");
                string subtitle = People.Utf8.GetString(WidgetString(game, subtitles[0], SubtitleText, 256));
                var screen = new OpenScreen();
                var match = IdSuffix.Match(subtitle);
                if (match.Success)
                {
                    screen.PersonId = match.Groups[1].Value;
                    screen.Title = subtitle.Substring(0, match.Index).Trim();
                }
                else
                {
                    (screen.PersonId, screen.Record) = PersonHandles.PersonRecordForReference(
                        game, PersonHandles.ScreenReference(game, manager, widgets));
                    screen.Title = subtitle.Trim();
                }
                var biographies = Named(widgets, "biot");
                if (biographies.Count > 1)
                    throw new MemoryUnavailableException("The FM24 screen shows more than one biography.");
                if (biographies.Count == 1)
                    screen.Biography = FirstParagraph(WidgetString(game, biographies[0], BiographyText, 16384));
                screen.Ages = Ages(game, widgets);
                screen.Today = Date(game, widgets);
                return screen;
            }
            catch (Exception error) when (People.Unreadable(error) || error is DecoderFallbackException)
            {
                throw new MemoryUnavailableException("Could not read the open FM24 screen: " + error.Message);
            }
        }

        // A short text widget's text, or null.
        private static string ShortText(GameProcess game, long widget, int least, int most)
        {
            long text = game.U64(widget + WidgetText);
            if (text == 0)
                return null;
            uint size = game.U32(text);
            if (size < least || size > most)
                return null;
            return Encoding.Latin1.GetString(game.Read(text + 4, (int)size));
        }

        // The in-game date FM24's top bar shows ("16 Sep 2023", its 'dtam'
        // widgets), or null if it isn't shown or its widgets disagree.
        public static DateTime? Date(GameProcess game, Dictionary<int, List<long>> widgets)
        {
            var dates = new HashSet<DateTime>();
            foreach (long widget in Named(widgets, "dtam"))
            {
                try
                {
                    var match = ShownDate.Match(ShortText(game, widget, 10, 11) ?? "");
                    if (match.Success)
                        dates.Add(new DateTime(int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                                               Array.IndexOf(Months, match.Groups[2].Value) + 1,
                                               int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
                }
                catch (Exception error) when (People.Unreadable(error))
                {
                }
            }
            return dates.Count == 1 ? dates.First() : (DateTime?)null;
        }

        // The in-game date shown on the open screen, or null.
        public static DateTime? ScreenDate(GameProcess game)
        {
            try
            {
                long manager = Widgets.UiManager(game);
                return Date(game, Tree(game, manager, game.U64(manager + ScreenRoot)));
            }
            catch (Exception error) when (error is MemoryUnavailableException || People.Unreadable(error))
            {
                return null;
            }
        }

        // The ages in the screen's "<n> years old" texts: the profile's, so its
        // age needn't be found by searching all of FM24's memory (seconds on macOS).
        public static HashSet<int> Ages(GameProcess game, Dictionary<int, List<long>> widgets)
        {
            var ages = new HashSet<int>();
            foreach (long widget in widgets.Values.SelectMany(group => group))
            {
                try
                {
                    var match = Age.Match(ShortText(game, widget, 10, 16) ?? "");
                    if (match.Success)
                        ages.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                }
                catch (Exception error) when (People.Unreadable(error))
                {
                }
            }
            return ages;
        }
    }
}
