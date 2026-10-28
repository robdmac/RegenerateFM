using System;
using System.Collections.Generic;
using SI.Bindable;
using UnityEngine;
using UnityEngine.UIElements;

namespace RegenerateFM.FM26
{
    // A club's staff list (its All Staff table) for Generate faces…, and the way
    // between it and the club's player list, which a batch that includes both
    // takes through the club's Club Site page.
    public sealed partial class Plugin
    {
        private enum ListKind { Players, Staff }

        private static string Plural(ListKind kind) => kind == ListKind.Staff ? "staff" : "players";

        private static ListKind Other(ListKind kind) => kind == ListKind.Staff ? ListKind.Players : ListKind.Staff;

        private Button _staffMenuButton;

        // The page on top: FM26 keeps a screen (Menu), the report opened from it
        // (Report) and a card over that (Card) laid out at once, one above the
        // other, so a page underneath still has a size and its rows can be read.
        private static VisualElement TopPage(VisualElement root)
        {
            if (root == null)
                return null;
            foreach (var layer in new[] { "Card", "Report", "Menu" })
            {
                VisualElement body = null;
                for (int i = 0; i < root.hierarchy.childCount && body == null; i++)
                {
                    var child = root.hierarchy.ElementAt(i);
                    if (child.name != layer)
                        continue;
                    for (int j = 0; j < child.hierarchy.childCount; j++)
                        if (child.hierarchy.ElementAt(j).name == "Body")
                            body = child.hierarchy.ElementAt(j);
                }
                VisualElement page = null;
                for (int i = 0; body != null && i < body.hierarchy.childCount; i++)
                {
                    var child = body.hierarchy.ElementAt(i);
                    if (child.worldBound.width > 0 && child.worldBound.height > 0)
                        page = child;
                }
                if (page != null)
                    return page;
            }
            return null;
        }

        // The All Staff table's rows (FM26's StaffContractsTable, on a club's
        // ClubAllStaffTool page: the Staff tile of another club's Club Site, or
        // the All Staff tab of your own club's Staff page).
        private static ScrollView StaffScroll(VisualElement root)
        {
            var table = FindNamed(TopPage(root), "StaffContractsTable");
            return table == null ? null : PeopleScroll(table);
        }

        private static ScrollView PeopleScroll(VisualElement element)
        {
            if (element == null)
                return null;
            var scroll = element.TryCast<ScrollView>();
            if (scroll != null && scroll.Q<VisualElement>("PersonImage") != null)
                return scroll;
            for (int i = 0; i < element.hierarchy.childCount; i++)
                if (PeopleScroll(element.hierarchy.ElementAt(i)) is ScrollView found)
                    return found;
            return null;
        }

        // How many people the table lists (it draws only the rows in view).
        private static int StaffCount(ScrollView list)
        {
            for (var element = list?.parent; element != null; element = element.parent)
                if (element.TryCast<StreamedListView>() is StreamedListView table)
                    return table.ItemCount;
            return 0;
        }

        // A staff row: portrait, then name, team, club job and age.
        private static SquadCandidate ReadStaffCandidate(VisualElement row)
        {
            var image = FindNamed(row, "PersonImage")?.TryCast<SI.UI.Image>();
            var values = TextValues(row);
            if (image == null || values.Count < 4)
                return null;
            int.TryParse(values[3], out int age);
            return new SquadCandidate
            {
                Kind = ListKind.Staff, Key = values[0] + "|" + values[2] + "|" + age,
                Name = values[0], Age = age, Address = image.m_imageAddressableID?.ToString() ?? ""
            };
        }

        private static ScrollView ListScroll(ListKind kind, VisualElement root) =>
            kind == ListKind.Staff ? StaffScroll(root) : FindScroll(SquadTable(root));

        private static SquadCandidate ReadCandidate(ListKind kind, VisualElement row) =>
            kind == ListKind.Staff ? ReadStaffCandidate(row) : ReadSquadCandidate(row);

        // The All Staff table has a Generate faces… button beside its Filter.
        private void CheckStaffMenu()
        {
            var root = CurrentRoot();
            var block = StaffScroll(root) == null ? null : FindNamed(TopPage(root), "StaffContracts");
            if (block == null)
            {
                RemoveStaffMenuButton();
                return;
            }
            if (_staffMenuButton != null && _staffMenuButton.parent == block)
            {
                string label = Text.T("Generate faces…");
                if (_staffMenuButton.text != label)
                    _staffMenuButton.text = label;
                return;
            }
            RemoveStaffMenuButton();
            var button = new Button((Il2CppSystem.Action)(System.Action)(() => StartSquadScan(ListKind.Staff)))
            {
                name = "regeneratefm-staff-command", text = Text.T("Generate faces…"), focusable = false
            };
            button.style.position = Position.Absolute;
            button.style.top = 13;
            button.style.right = 100;
            button.style.height = 32;
            button.style.color = Color.white;
            button.style.backgroundColor = new Color(0.18f, 0.13f, 0.24f, 1f);
            button.style.paddingLeft = 12;
            button.style.paddingRight = 12;
            block.Add(Directed(button));
            _staffMenuButton = button;
            Log.LogInfo("RegenerateFM staff command added to the All Staff table");
        }

        private void RemoveStaffMenuButton()
        {
            if (_staffMenuButton != null && _staffMenuButton.parent != null)
                _staffMenuButton.RemoveFromHierarchy();
            _staffMenuButton = null;
        }

        // A click where the element is, as the pointer would make it: a staff
        // row's name isn't a Button, and opens the person when clicked. FM26
        // routes it to what's under the pointer, so RegenerateFM's dialog lets it
        // through meanwhile.
        private static void PointerClick(VisualElement element)
        {
            var center = element.worldBound.center;
            var dialog = Instance?._squadDialog;
            var picking = new List<(VisualElement, PickingMode)>();
            void LetThrough(VisualElement e)
            {
                picking.Add((e, e.pickingMode));
                e.pickingMode = PickingMode.Ignore;
                for (int i = 0; i < e.hierarchy.childCount; i++)
                    LetThrough(e.hierarchy.ElementAt(i));
            }
            if (dialog?.parent != null)
                LetThrough(dialog);
            try
            {
                var target = (element.panel.Pick(center) ?? element).Cast<IEventHandler>();
                foreach (var type in new[] { EventType.MouseDown, EventType.MouseUp })
                {
                    var systemEvent = new Event { type = type, mousePosition = center, button = 0, clickCount = 1 };
                    EventBase pointer = type == EventType.MouseDown
                        ? PointerEventBase<PointerDownEvent>.GetPooled(systemEvent)
                        : PointerEventBase<PointerUpEvent>.GetPooled(systemEvent);
                    pointer.target = target;
                    element.panel.visualTree.SendEvent(pointer);
                }
            }
            finally
            {
                foreach (var (e, mode) in picking)
                    e.pickingMode = mode;
            }
        }

        private static bool ClickButtonAbove(VisualElement element)
        {
            for (var e = element; e != null; e = e.parent)
                if (e.TryCast<Button>() is Button button)
                {
                    button.clickable.SimulateSingleClick(null);
                    return true;
                }
            return false;
        }

        // One step from where FM26 is towards the club's list of this kind, or
        // what's in the way. Each list's header has the club's badge, which opens
        // its Club Site; that page's squad tile opens the club's first team, and
        // its Staff tile the All Staff table (for your own club, a Staff Report
        // card first, then the Staff page, whose All Staff tab has the table).
        private string StepToList(ListKind kind)
        {
            var root = CurrentRoot();
            var top = TopPage(root);
            if (top == null)
                return "no page is shown";
            if (kind == ListKind.Staff)
            {
                if (top.name == "StaffReportCard" && FindLabel(top, "Staff", StaffTitle) is VisualElement title)
                {
                    PointerClick(title);
                    return "opened the Staff Report's staff";
                }
                if (top.name == "ClubAllStaffTool" && FindLabel(top, "All Staff", AllStaffTab) is VisualElement tab && ClickButtonAbove(tab))
                    return "opened the All Staff tab";
                // The Staff tile is ClubStaff, or at some clubs (Barnsley's)
                // ClubAllStaffAI, a carousel whose shown page has the title.
                if (top.name == "ClubSite" && (FindNamed(FindNamed(top, "ClubStaff"), "title-text-alt") ??
                                               FindNamed(FindNamed(top, "ClubAllStaffAI"), "title-text")) is VisualElement staff)
                {
                    PointerClick(staff);
                    return "opened Club Site's Staff";
                }
            }
            else if (top.name == "ClubSite" && FindNamed(FindNamed(top, "FirstTeam"), "title-text") is VisualElement team)
            {
                PointerClick(team);
                return "opened Club Site's first team";
            }
            if (top.name != "ClubSite" && (ListScroll(Other(kind), root) != null) && FindNamed(top, "ClubButton") is VisualElement badge)
            {
                PointerClick(badge);
                return "opened the club's Club Site";
            }
            return "can't go on from " + top.name;
        }
    }
}
