using System;
using System.Collections.Generic;
using System.Net;
using SI.Bindable;
using SI.Translation;
using SI.VisualScripting;
using UnityEngine.UIElements;

namespace RegenerateFM.FM26
{
    // FM26 in another language. Its labels carry the ID of the text they show,
    // which FM26 translates into the player's language, so a label is found by
    // its English text or by FM26's translation of its ID. The IDs are those of
    // the checked 26.3.2 builds (GameBuild.cs). Values the prompt uses (nation,
    // position, job, attribute names) stay in the player's language.
    public sealed partial class Plugin
    {
        internal const int HeightLabel = 20919;
        internal const int ActionsLabel = 15539;
        internal const int StaffTitle = 23144;
        internal const int AllStaffTab = 12243;
        // The attribute panel's column headings. A goalkeeper's panel has its
        // own Goalkeeping and Technical headings (checked in Arabic on Linux).
        private const int TechnicalHeading = 4900, SetPiecesHeading = 4904, MentalHeading = 4901,
                          PhysicalHeading = 4905, GoalkeepingHeading = 4909,
                          KeeperGoalkeepingHeading = 4908, KeeperTechnicalHeading = 4907;

        private static readonly Dictionary<int, string> GameTexts = new Dictionary<int, string>();
        private bool _languageUnreadable;

        // RegenerateFM's own text follows FM26's interface language (Text), and
        // FM26's texts for label IDs are read again once it changes.
        internal void SyncLanguage()
        {
            try
            {
                string language = Text.Language;
                Text.Language = TranslationManager.CurrentLanguage;
                if (Text.Language != language)
                    GameTexts.Clear();
            }
            catch (Exception error)
            {
                if (!_languageUnreadable)
                    Log.LogWarning("FM26's language could not be read: " + error.Message);
                _languageUnreadable = true;
            }
        }

        // FM26's text for a label ID in the player's language, or null.
        internal static string GameText(int id)
        {
            if (GameTexts.TryGetValue(id, out var cached))
                return cached;
            string text = null;
            try
            {
                text = new TranslationID(id, null).TranslatedString;
                text = string.IsNullOrWhiteSpace(text) ? null : WebUtility.HtmlDecode(Tags.Replace(text, "")).Trim();
            }
            catch (Exception error)
            {
                Instance?.Log.LogWarning("FM26 text " + id + " could not be read: " + error.Message);
            }
            GameTexts[id] = text;
            return text;
        }

        // RegenerateFM's elements, with their text written right to left in
        // Arabic: UI Toolkit's advanced text generator joins and orders its
        // letters, as FM26 does for its own text. Applied to every element, as
        // the generator style isn't inherited. Text set to start at the left
        // (a menu item's) starts at the right instead, as FM26's own does.
        internal static VisualElement Directed(VisualElement element)
        {
            if (element == null || !Text.RightToLeft)
                return element;
            element.languageDirection = LanguageDirection.RTL;
            element.style.unityTextGenerator = UnityEngine.TextGeneratorType.Advanced;
            var align = element.style.unityTextAlign;
            if (align.keyword == StyleKeyword.Undefined && align.value is UnityEngine.TextAnchor.UpperLeft or UnityEngine.TextAnchor.MiddleLeft or UnityEngine.TextAnchor.LowerLeft)
            {
                element.style.unityTextAlign = align.value + 2;
                var left = element.style.paddingLeft;
                element.style.paddingLeft = element.style.paddingRight;
                element.style.paddingRight = left;
            }
            for (int i = 0; i < element.hierarchy.childCount; i++)
                Directed(element.hierarchy.ElementAt(i));
            return element;
        }

        // Whether a label's text is this English one or FM26's text for its ID.
        private static bool IsLabel(string value, string english, int id) =>
            value == english || value != null && value == GameText(id);

        // The shown element with this English text or FM26's text for its ID.
        private static VisualElement FindLabel(VisualElement element, string english, int id)
        {
            var found = FindText(element, english);
            if (found != null)
                return found;
            var text = GameText(id);
            return text != null && text != english ? FindText(element, text) : null;
        }

        // The person's age, from the header label whose text FM26 makes from
        // their age ("16 years old", "16 Jahre", "16 y/o (3/26/2008)"): its first
        // number. ``text`` is that label's text. 0 if there is none.
        private static int BoundAge(VisualElement element, out string text)
        {
            text = null;
            if (element == null || element.worldBound.width <= 0)
                return 0;
            var label = element.TryCast<SIText>();
            if (label != null && !string.IsNullOrEmpty(label.text) && BindsAge(label))
            {
                text = WebUtility.HtmlDecode(Tags.Replace(label.text, "")).Trim();
                int age = 0;
                foreach (char c in text)
                {
                    int digit = (int)char.GetNumericValue(c);
                    if (char.IsDigit(c) && digit >= 0)
                        age = age * 10 + digit;
                    else if (age > 0)
                        break;
                    if (age > 999)
                        break;
                }
                if (age > 0)
                    return age;
            }
            for (int i = 0; i < element.hierarchy.childCount; i++)
            {
                int age = BoundAge(element.hierarchy.ElementAt(i), out text);
                if (age > 0)
                    return age;
            }
            text = null;
            return 0;
        }

        private static bool BindsAge(SIText label)
        {
            try
            {
                var binding = label.m_textBinding;
                if (binding.Kind != BindingMethodKind.VisualFunction)
                    return false;
                var inputs = binding.VisualFunction.Inputs;
                for (int i = 0; i < inputs.Count; i++)
                {
                    var list = inputs[i]?.TryCast<VisualArg<SI.Core.StructList<TranslationArg>>>();
                    if (list == null)
                        continue;
                    var items = list.Get();
                    for (int j = 0; j < items.Count; j++)
                    {
                        // "Person.Age" on a player's header, "person.age" on a staff member's.
                        if (string.Equals(items[j].Binding.ToString(), "Person.Age", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch (Exception error)
            {
                Instance?.Log.LogWarning("An FM26 label's binding could not be read: " + error.Message);
            }
            return false;
        }

        // The translation ID of the text an FM26 label shows, or 0.
        private static int LabelId(VisualElement element)
        {
            var label = element?.TryCast<SIText>();
            if (label == null)
                return 0;
            try
            {
                var binding = label.m_textBinding;
                if (binding.Kind != BindingMethodKind.VisualFunction)
                    return 0;
                var inputs = binding.VisualFunction.Inputs;
                for (int i = 0; i < inputs.Count; i++)
                    if (inputs[i]?.TryCast<VisualArg<TranslationID>>() is VisualArg<TranslationID> id)
                        return id.Get().GameID;
            }
            catch (Exception error)
            {
                Instance?.Log.LogWarning("An FM26 label's binding could not be read: " + error.Message);
            }
            return 0;
        }

        // The attribute panel's column headings by section ("Technical",
        // "Mental", "Physical", "Goalkeeping"), found by their IDs.
        private static List<(VisualElement Heading, string Section)> AttributeHeadings(VisualElement block)
        {
            var headings = new List<(VisualElement, string)>();
            CollectHeadings(block, headings);
            return headings;
        }

        private static void CollectHeadings(VisualElement element, List<(VisualElement, string)> headings)
        {
            if (element == null || element.worldBound.width <= 0)
                return;
            if (element.TryCast<SIText>() != null && element.name != null)
            {
                string section = LabelId(element) switch
                {
                    TechnicalHeading or SetPiecesHeading or KeeperTechnicalHeading => "Technical",
                    MentalHeading => "Mental",
                    PhysicalHeading => "Physical",
                    GoalkeepingHeading or KeeperGoalkeepingHeading => "Goalkeeping",
                    _ => null,
                };
                if (section != null)
                    headings.Add((element, section));
            }
            for (int i = 0; i < element.hierarchy.childCount; i++)
                CollectHeadings(element.hierarchy.ElementAt(i), headings);
        }

        // The section of an attribute row: that of the only heading in the
        // smallest panel that holds both, or null.
        private static string AttributeSection(VisualElement row, List<(VisualElement Heading, string Section)> headings)
        {
            for (var panel = row.parent; panel != null; panel = panel.parent)
            {
                string section = null;
                int count = 0;
                foreach (var (heading, name) in headings)
                    if (panel.Contains(heading))
                    {
                        count++;
                        section = name;
                    }
                if (count == 1)
                    return section;
                if (count > 1)
                    return null;
            }
            return null;
        }

        // Squad's tabs (Overview, First Team, then the club's other teams) as
        // buttons in a row, and the First Team tab's label: the second.
        private static (VisualElement Tabs, VisualElement FirstTeam) SquadTabs(VisualElement root)
        {
            var switches = new List<VisualElement>();
            CollectNamed(root, "ButtonOrDropdownSwitch", switches);
            foreach (var tab in switches)
            {
                for (var row = tab.parent; row != null; row = row.parent)
                {
                    if (row.name != "View")
                        continue;
                    var tabs = switches.FindAll(other => row.Contains(other));
                    if (tabs.Count >= 3 && FirstText(tabs[1]) != null)
                        return (row, FindText(tabs[1], FirstText(tabs[1])));
                    break;
                }
            }
            return (null, null);
        }

        // The open dropdown of FM26's Squad navigation button, found by where it
        // opens: under SquadButton, whatever the language.
        private static VisualElement SquadDropdownList(VisualElement root)
        {
            var squad = FindNamed(root, "SquadButton");
            var floating = FindNamed(root, "floating-element-root");
            if (squad == null || floating == null)
                return null;
            var lists = new List<VisualElement>();
            CollectNamed(floating, "View", lists);
            foreach (var list in lists)
            {
                var bounds = list.worldBound;
                if (bounds.width >= 100 && Math.Abs(bounds.x - squad.worldBound.x) < 60 &&
                    bounds.y >= squad.worldBound.y && bounds.y < squad.worldBound.yMax + 60 && list.childCount >= 8)
                    return list;
            }
            return null;
        }
    }
}
