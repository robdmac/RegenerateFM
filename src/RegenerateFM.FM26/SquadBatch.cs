using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using SI.Bindable;
using SI.Core;
using SI.Input;
using SI.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace RegenerateFM.FM26
{
    // The Squad menu's Generate faces… (and the All Staff table's, StaffBatch.cs):
    // scanning the list and making the missing faces one at a time. Either
    // confirmation can include the club's other list, players or staff, which is
    // counted first and done after the list the batch starts on.
    public sealed partial class Plugin
    {
        private sealed class SquadScan
        {
            public ListKind Kind;
            // Still on the way to the list (the other list, for the confirmation's tick box).
            public bool Finding;
            public System.DateTime StartedUtc = System.DateTime.UtcNow;
            public System.DateTime NextStepUtc;
            // Counting the other list for this confirmation.
            public SquadOffer Offer;
            public ScrollView List;
            public float OriginalY;
            public System.DateTime TopReadyUtc;
            public bool AwaitTop = true;
            public float LastY = -1;
            public int Steps;
            public int ExpectedCount;
            public int Passes = 1;
            public readonly Dictionary<string, SquadCandidate> Portraits = new Dictionary<string, SquadCandidate>(StringComparer.Ordinal);
            public readonly HashSet<string> Missing = new HashSet<string>(StringComparer.Ordinal);
            public Label Status;
        }

        private sealed class SquadCandidate
        {
            public ListKind Kind;
            public string Key;
            public string Name;
            public int Age;
            public string Address;
        }

        private enum SquadStage { FindList, FindPlayer, AwaitProfile, Request, Refresh, ReturnToSquad, AwaitSquad }

        // A confirmation's lists: the one scanned, and the club's other list once counted.
        private sealed class SquadOffer
        {
            public ListKind Kind;
            public List<SquadCandidate> Missing;
            public int Count;
            public List<SquadCandidate> OtherMissing;
            public int OtherCount;
            public string OtherProblem;  // why the other list couldn't be counted
        }

        private sealed class SquadBatch
        {
            public readonly List<SquadCandidate> Queue = new List<SquadCandidate>();
            public int Index;
            public int Completed;
            public SquadStage Stage = SquadStage.FindPlayer;
            public System.DateTime StageStartedUtc = System.DateTime.UtcNow;
            public ScrollView List;
            public float LastY = -1;
            public int SearchSteps;
            public Task<GenerationResult> Request;
            public ImageSettings Settings;
            public long PersonId;
            public int GraphicsSerial;
            public Label Status;
            public bool StopAfterCurrent;
            public System.DateTime NextPlayerUtc;
            public System.DateTime? UnfocusedSinceUtc;
            // A TeamSquadReport page's team ("Atlanta United Academy"): Squad opens
            // the Overview, so the batch goes on to this team's tab from there.
            // Null when the batch started on Squad > Overview's own table.
            public string ReturnTab;
            // A team page rather than Squad > Overview's own table (a youth tab,
            // another club's team, or the club's women's team from its club page):
            // return to it with FM26's Back button.
            public bool ReturnByBack;
            public bool TabClicked;
            public int NeedsScouting;
            // Staff whose names don't open a profile (an owner, for one).
            public readonly List<string> Unopened = new List<string>();
            public System.DateTime NextStepUtc;
            public ListKind Kind => Queue[Index].Kind;
        }

        private void CheckSquadMenu()
        {
            var root = CurrentRoot();
            var table = SquadTable(root);
            if (table == null || FindScroll(table) == null)
            {
                // Off the squad pages this is the normal case, so only say why on a
                // page that has a player table without the rows RegenerateFM reads.
                if (table != null)
                    NoteSquadMenu("the squad table has no scrolling list of NameButtons: " + Ancestry(table));
                else
                    _squadMenuNote = null;
                RemoveSquadMenuButton();
                return;
            }
            // The top Squad navigation dropdown is a floating, virtualized View.
            // "Squad Numbers" uniquely identifies this menu on the checked build.
            // Reading every text on the page is slow, so only the floating layer
            // that FM26's dropdowns open in is read.
            var menuItem = FindText(FindNamed(root, "floating-element-root") ?? root, "Squad Numbers");
            var menuList = menuItem;
            while (menuList != null && menuList.name != "View")
                menuList = menuList.parent;
            if (menuList == null || menuList.worldBound.width < 100)
            {
                // A closed dropdown is the usual case; only odd matches are logged.
                if (menuItem != null)
                    NoteSquadMenu(menuList == null ? "\"Squad Numbers\" has no parent named View: " + Ancestry(menuItem)
                    : "the dropdown's View is " + menuList.worldBound.width + " px wide");
                RemoveSquadMenuButton();
                return;
            }
            var scroll = menuList.parent?.TryCast<ScrollView>();
            if (scroll == null)
            {
                NoteSquadMenu("the dropdown's View isn't in a ScrollView: " + Ancestry(menuList));
                return;
            }
            NoteSquadMenu("dropdown found");
            // FM26 closes the dropdown when the pointer leaves it, so the item goes
            // inside the dropdown, after its list: moving to it never leaves the menu.
            var dropdown = scroll.parent;
            if (_squadMenuButton != null && _squadMenuButton.parent != null && _squadMenuList == menuList)
            {
                if (_squadMenuButton.parent == root)
                    PositionSquadMenuButton(_squadMenuButton, scroll, root);
                return;
            }
            RemoveSquadMenuButton();
            var button = new Button((Il2CppSystem.Action)(System.Action)(() => StartSquadScan(ListKind.Players)))
            {
                name = "regeneratefm-squad-command", text = "Generate faces…", focusable = false
            };
            button.style.height = 36;
            button.style.flexShrink = 0;
            button.style.color = Color.white;
            button.style.backgroundColor = new Color(0.18f, 0.13f, 0.24f, 1f);
            button.style.unityTextAlign = TextAnchor.MiddleLeft;
            button.style.paddingLeft = 12;
            if (dropdown != null)
            {
                dropdown.Add(button);
            }
            else
            {
                button.style.position = Position.Absolute;
                PositionSquadMenuButton(button, scroll, root);
                root.Add(button);
            }
            _squadMenuButton = button;
            _squadMenuList = menuList;
            var chain = new List<string>();
            for (VisualElement element = scroll; element != null && chain.Count < 6; element = element.parent)
                chain.Add((element.name ?? "") + " " + element.GetType().Name + " " + element.worldBound);
            Log.LogInfo("RegenerateFM squad command added to Squad menu " + (dropdown != null ? "(inside it)" : "(below it)") +
                        "; dropdown: " + string.Join(" < ", chain));
        }

        private static void PositionSquadMenuButton(VisualElement button, ScrollView scroll, VisualElement root)
        {
            button.style.width = scroll.worldBound.width;
            button.style.left = scroll.worldBound.x - root.worldBound.x;
            button.style.top = scroll.worldBound.yMax - root.worldBound.y + 2;
        }

        // Why the squad dropdown has (or hasn't) the command, logged when it changes,
        // so a build whose squad screens differ shows where the match stops.
        private string _squadMenuNote;

        private void NoteSquadMenu(string note)
        {
            if (note == _squadMenuNote)
                return;
            _squadMenuNote = note;
            Log.LogInfo("Squad menu: " + note);
        }

        private static string Ancestry(VisualElement element)
        {
            var names = new List<string>();
            for (var e = element; e != null && names.Count < 70; e = e.parent)
                names.Add((string.IsNullOrEmpty(e.name) ? "?" : e.name) + ":" + e.GetType().Name);
            return string.Join(" < ", names);
        }

        private void RemoveSquadMenuButton()
        {
            if (_squadMenuButton != null && _squadMenuButton.parent != null)
                _squadMenuButton.RemoveFromHierarchy();
            _squadMenuButton = null;
            _squadMenuList = null;
        }

        // The squad's player table. FM26 26.3.2 shows it on Squad > Overview
        // (SquadScreen > PlayerTableContainer). The Demo's Overview is a tile
        // dashboard instead, which stays in the tree after you leave it; its
        // First Team and Under 21s pages are a TeamSquadReport with a "playertable".
        // FM26 26.3.2 keeps Squad > Overview laid out under a team page, and reuses one
        // TeamSquadReport for every team (hidden, display None, when not shown), so a
        // shown team page is preferred.
        private static VisualElement SquadTable(VisualElement root)
        {
            // Only the page on top: a squad page left beneath the staff list isn't shown.
            root = TopPage(root) ?? root;
            if (FindNamed(root, "TeamSquadReport") is VisualElement report && FindNamed(report, "playertable") is VisualElement teamTable)
                return teamTable;
            if (FindNamed(root, "SquadScreen") != null && FindNamed(root, "PlayerTableContainer") is VisualElement table)
                return table;
            return null;
        }

        // Squad > Overview's own table is inside its SquadScreen; a team page's
        // isn't, even when Squad > Overview, once opened, stays laid out beside it.
        private static bool InSquadScreen(VisualElement element)
        {
            for (var e = element; e != null; e = e.parent)
                if (e.name == "SquadScreen")
                    return true;
            return false;
        }

        // The batch's player list: on the team page it started from (ReturnTab), else Squad > Overview's.
        private static ScrollView BatchList(SquadBatch batch)
        {
            var root = CurrentRoot();
            if (batch.Kind == ListKind.Staff)
                return StaffScroll(root);
            if (batch.ReturnTab != null && SquadPageTeam(root) != batch.ReturnTab)
                return null;
            return FindScroll(SquadTable(root));
        }

        private static ScrollView FindScroll(VisualElement element)
        {
            if (element == null)
                return null;
            var scroll = element.TryCast<ScrollView>();
            if (scroll != null && FindNamed(scroll, "NameButton") != null)
                return scroll;
            for (int i = 0; i < element.hierarchy.childCount; i++)
            {
                var found = FindScroll(element.hierarchy.ElementAt(i));
                if (found != null)
                    return found;
            }
            return null;
        }

        private void StartSquadScan(ListKind kind)
        {
            if (_pending != null || WaitingForGraphics || _squadScan != null || _squadBatch != null)
            {
                Log.LogWarning("Squad scan cannot start while another RegenerateFM operation is active");
                return;
            }
            if (!KeyReady(() => StartSquadScan(kind)))
                return;
            try
            {
                CurrentSettings();
            }
            catch (Exception error)
            {
                Log.LogWarning(error.Message);
                ShowDialog(error.Message, "Close", () => CancelSquadScan());
                return;
            }
            var list = ListScroll(kind, CurrentRoot());
            if (list == null)
            {
                Log.LogWarning("The " + Plural(kind) + " list was not found; no image request was made");
                return;
            }
            var status = ShowDialog("Scanning " + (kind == ListKind.Staff ? "staff" : "squad") + " portraits…", "Cancel",
                () => CancelSquadScan());
            _squadScan = new SquadScan { Kind = kind, Status = status };
            BeginScan(_squadScan, list);
        }

        // How many the list should have: FM26's count for the staff table; for a
        // squad, "Players (28)" on the first team, "U21 Squad Players (21)" on youth
        // teams. 0 if not found: the scan still runs, but nothing can be generated.
        private int ExpectedCount(ListKind kind, ScrollView list)
        {
            if (kind == ListKind.Staff)
                return StaffCount(list);
            var table = SquadTable(CurrentRoot());
            if (table == null)
                return 0;
            foreach (var value in TextValues(table.parent))
            {
                var match = Regex.Match(value, @"^(?:Senior Squad |U\d{2} Squad |Squad )?Players\s*\((\d+)\)$");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int expected))
                    return expected;
            }
            var texts = TextValues(table.parent);
            Log.LogWarning("Squad player count was not found near the table; texts: " +
                string.Join(" | ", texts.GetRange(0, Math.Min(40, texts.Count))));
            return 0;
        }

        private void BeginScan(SquadScan scan, ScrollView list)
        {
            scan.Finding = false;
            scan.List = list;
            scan.OriginalY = list.scrollOffset.y;
            scan.ExpectedCount = ExpectedCount(scan.Kind, list);
            scan.TopReadyUtc = System.DateTime.UtcNow.AddSeconds(1);
            list.scrollOffset = new Vector2(list.scrollOffset.x, 0);
            Log.LogInfo("RegenerateFM " + Plural(scan.Kind) + " portrait scan started (" + scan.ExpectedCount + " listed)");
        }

        private void PollSquadScan()
        {
            var scan = _squadScan;
            if (scan == null)
                return;
            if (scan.Finding)
            {
                FindScanList(scan);
                return;
            }
            if (scan.List.parent == null || ListScroll(scan.Kind, CurrentRoot()) == null)
            {
                if (scan.Offer != null)
                    OtherListFailed(scan, "its " + Plural(scan.Kind) + " list closed while it was being read");
                else
                    CancelSquadScan();
                return;
            }
            if (scan.AwaitTop)
            {
                if (scan.List.scrollOffset.y > 2f)
                    scan.List.scrollOffset = new Vector2(scan.List.scrollOffset.x, 0);
                if (scan.List.scrollOffset.y > 2f || System.DateTime.UtcNow < scan.TopReadyUtc)
                    return;
                scan.AwaitTop = false;
            }
            var listView = FindNamed(scan.List, "View");
            if (listView == null)
                throw new InvalidOperationException("Squad rows disappeared while scanning");
            for (int i = 0; i < listView.hierarchy.childCount; i++)
            {
                var row = listView.hierarchy.ElementAt(i);
                var candidate = ReadCandidate(scan.Kind, row);
                if (candidate == null)
                    continue;
                scan.Portraits[candidate.Key] = candidate;
                if (IsDefaultPortrait(candidate.Address))
                    scan.Missing.Add(candidate.Key);
                else
                    scan.Missing.Remove(candidate.Key);
            }
            scan.Steps++;
            if (scan.Status != null)
                scan.Status.text = "Scanning portraits… " + scan.Portraits.Count + " " + Plural(scan.Kind) + " checked";
            float currentY = scan.List.scrollOffset.y;
            float maxY = Math.Max(0f, scan.List.contentContainer.layout.height -
                                    scan.List.contentViewport.layout.height);
            if (scan.Steps > 200)
                throw new InvalidOperationException("Squad scan exceeded 200 steps");
            if (currentY >= maxY - 2f || (scan.LastY >= 0 && Math.Abs(currentY - scan.LastY) < 1f))
            {
                // Rows that were still blank (names arrive after the rows on the
                // Demo) were skipped; another pass from the top picks them up.
                if (scan.ExpectedCount > 0 && scan.Portraits.Count < scan.ExpectedCount && scan.Passes < 3)
                {
                    scan.Passes++;
                    scan.LastY = -1;
                    scan.AwaitTop = true;
                    scan.TopReadyUtc = System.DateTime.UtcNow.AddSeconds(1);
                    scan.List.scrollOffset = new Vector2(scan.List.scrollOffset.x, 0);
                    Log.LogInfo("Scan read " + scan.Portraits.Count + " of " + scan.ExpectedCount + " " +
                                Plural(scan.Kind) + "; scanning again (pass " + scan.Passes + ")");
                    return;
                }
                scan.List.scrollOffset = new Vector2(scan.List.scrollOffset.x, scan.OriginalY);
                _squadScan = null;
                Log.LogInfo("RegenerateFM " + Plural(scan.Kind) + " scan finished: " + scan.Portraits.Count +
                            " of " + scan.ExpectedCount + ", " + scan.Missing.Count + " missing portraits");
                bool complete = scan.ExpectedCount > 0 && scan.Portraits.Count == scan.ExpectedCount;
                var missing = new List<SquadCandidate>();
                foreach (var key in scan.Missing)
                    missing.Add(scan.Portraits[key]);
                missing.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                if (scan.Offer != null)
                {
                    if (!complete)
                    {
                        OtherListFailed(scan, "only " + scan.Portraits.Count + " of its " + scan.ExpectedCount + " " +
                                              Plural(scan.Kind) + " could be read");
                        return;
                    }
                    scan.Offer.OtherMissing = missing;
                    scan.Offer.OtherCount = scan.Portraits.Count;
                    ShowSquadConfirmation(scan.Offer, true);
                    return;
                }
                if (!complete)
                {
                    if (scan.Status != null)
                        scan.Status.text = "Scan incomplete (" + scan.Portraits.Count + " of " + scan.ExpectedCount + " " +
                                           Plural(scan.Kind) + "). No images have been requested.";
                    return;
                }
                ShowSquadConfirmation(new SquadOffer { Kind = scan.Kind, Missing = missing, Count = scan.Portraits.Count }, false);
                return;
            }
            scan.LastY = currentY;
            scan.List.scrollOffset = new Vector2(scan.List.scrollOffset.x,
                Math.Min(maxY, currentY + Math.Max(60f, scan.List.contentViewport.layout.height * 0.35f)));
        }

        private static SquadCandidate ReadSquadCandidate(VisualElement row)
        {
            var name = FirstText(FindNamed(row, "NameButton"));
            var image = FindNamed(row, "PersonImage")?.TryCast<SI.UI.Image>();
            if (string.IsNullOrWhiteSpace(name) || image == null)
                return null;
            // Position and age follow the name. Cells before it, such as First Team's
            // Inf tags ("Wnt"), can be drawn after the row, so the key starts at the name.
            var values = TextValues(row);
            int at = values.IndexOf(name);
            var position = at >= 0 && at + 1 < values.Count ? values[at + 1] : "";
            int.TryParse(at >= 0 && at + 2 < values.Count ? values[at + 2] : "", out int age);
            return new SquadCandidate
            {
                Key = name + "|" + position + "|" + age,
                Name = name, Age = age, Address = image.m_imageAddressableID?.ToString() ?? ""
            };
        }

        // "12 of 23 players have no portrait", with a tick box for the club's other
        // list. Ticking it counts that list first (FindScanList), so the faces of
        // both are in the total before anything is requested.
        private void ShowSquadConfirmation(SquadOffer offer, bool include)
        {
            include = include && offer.OtherMissing != null;
            var missing = new List<SquadCandidate>(offer.Missing);
            if (include)
                missing.AddRange(offer.OtherMissing);
            string text = MissingText(offer.Missing.Count, offer.Count, offer.Kind, false);
            if (include)
                text += " " + MissingText(offer.OtherMissing.Count, offer.OtherCount, Other(offer.Kind), true);
            else if (offer.OtherProblem != null)
                text += " Couldn't count " + OtherList(Other(offer.Kind)) + ": " + offer.OtherProblem + ".";
            text += missing.Count == 0 ? " No images have been requested."
                : " Generate " + Faces(missing.Count) + "? Each face makes one billed API request.";
            ShowDialog(text, "Cancel", () => CancelSquadScan());
            int at = 1;
            var other = Plural(Other(offer.Kind));
            _squadDialog.Insert(at++, TickBox("Include " + OtherList(Other(offer.Kind)) + (include ? "" : offer.OtherMissing != null
                ? " (" + offer.OtherMissing.Count + " without a portrait)" : ""), include, () =>
            {
                if (include)
                    ShowSquadConfirmation(offer, false);
                else if (offer.OtherMissing != null)
                    ShowSquadConfirmation(offer, true);
                else
                    CountOtherList(offer);
            }));
            if (missing.Count > 0)
            {
                var generate = new Button((Il2CppSystem.Action)(System.Action)(() => StartSquadBatch(missing)))
                { text = "Generate " + Faces(missing.Count), focusable = false };
                generate.style.height = 34;
                generate.style.marginTop = 10;
                generate.style.color = Color.white;
                generate.style.backgroundColor = new Color(0.38f, 0.10f, 0.60f, 1f);
                _squadDialog.Insert(at, generate);
            }
            Log.LogInfo("RegenerateFM confirmation ready: " + offer.Missing.Count + " " + Plural(offer.Kind) +
                        (include ? " and " + offer.OtherMissing.Count + " " + other : "") + " missing portraits");
        }

        private static string Faces(int count) => count + (count == 1 ? " face" : " faces");

        // "12 of 23 players have no portrait", "All of the club's 19 staff have portraits."
        private static string MissingText(int missing, int count, ListKind kind, bool other)
        {
            string people = !other ? count + " " + Plural(kind)
                : kind == ListKind.Staff ? "the club's " + count + " staff" : "the first team's " + count + " players";
            return missing == 0 ? (other ? "All of " : "All ") + people + " have portraits."
                : missing + " of " + people + (missing == 1 ? " has" : " have") + " no portrait.";
        }

        // A tick box as a row: FM26's panels don't style UI Toolkit's Toggle.
        private static VisualElement TickBox(string text, bool ticked, Action toggled)
        {
            var row = new Button((Il2CppSystem.Action)(System.Action)(toggled)) { text = "", focusable = false };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 12;
            row.style.paddingLeft = 0;
            row.style.backgroundColor = new Color(0, 0, 0, 0);
            row.style.borderTopWidth = row.style.borderBottomWidth = row.style.borderLeftWidth = row.style.borderRightWidth = 0;
            var box = new VisualElement { pickingMode = PickingMode.Ignore };
            box.style.width = box.style.height = 16;
            box.style.marginRight = 8;
            box.style.flexShrink = 0;
            box.style.borderTopWidth = box.style.borderBottomWidth = box.style.borderLeftWidth = box.style.borderRightWidth = 2;
            box.style.borderTopColor = box.style.borderBottomColor = box.style.borderLeftColor = box.style.borderRightColor = Color.white;
            box.style.backgroundColor = ticked ? new Color(0.62f, 0.36f, 0.95f, 1f) : new Color(0, 0, 0, 0);
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.style.color = Color.white;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 1;
            row.Add(box);
            row.Add(label);
            return row;
        }

        // Ticking the box: go to the club's other list and read it.
        private void CountOtherList(SquadOffer offer)
        {
            var kind = Other(offer.Kind);
            var status = ShowDialog("Opening " + OtherList(kind) + "…", "Cancel", () => CancelSquadScan());
            _squadScan = new SquadScan { Kind = kind, Offer = offer, Status = status, Finding = true };
            Log.LogInfo("RegenerateFM counting the club's " + Plural(kind));
        }

        private void FindScanList(SquadScan scan)
        {
            var list = ListScroll(scan.Kind, CurrentRoot());
            var now = System.DateTime.UtcNow;
            if (list != null && now >= scan.NextStepUtc)
            {
                BeginScan(scan, list);
                return;
            }
            if (now - scan.StartedUtc > TimeSpan.FromSeconds(30))
            {
                OtherListFailed(scan, "its " + Plural(scan.Kind) + " list didn't open");
                return;
            }
            if (list != null || now < scan.NextStepUtc)
                return;
            var step = StepToList(scan.Kind);
            Log.LogInfo("Finding the club's " + Plural(scan.Kind) + ": " + step);
            // A page takes a moment to open and fill in.
            scan.NextStepUtc = now.AddSeconds(2);
        }

        private void OtherListFailed(SquadScan scan, string problem)
        {
            Log.LogWarning("RegenerateFM couldn't count the club's " + Plural(scan.Kind) + ": " + problem);
            _squadScan = null;
            scan.Offer.OtherProblem = problem;
            ShowSquadConfirmation(scan.Offer, false);
        }

        private void StartSquadBatch(List<SquadCandidate> missing)
        {
            if (missing.Count == 0 || _pending != null || WaitingForGraphics || _squadBatch != null)
                return;
            var root = CurrentRoot();
            // The list on screen first, then the other.
            var shown = StaffScroll(root) != null ? ListKind.Staff : ListKind.Players;
            var queue = new List<SquadCandidate>(missing.FindAll(candidate => candidate.Kind == shown));
            queue.AddRange(missing.FindAll(candidate => candidate.Kind != shown));
            var list = ListScroll(queue[0].Kind, root);
            ImageSettings settings;
            try
            {
                settings = CurrentSettings();
            }
            catch (Exception error)
            {
                Log.LogWarning(error.Message);
                ShowDialog(error.Message, "Close", () => CancelSquadScan());
                return;
            }
            var status = ShowDialog("Opening the first missing " + (queue[0].Kind == ListKind.Staff ? "member of staff" : "player") + "…",
                "Stop after current face", () => RequestStopSquadBatch());
            var batch = new SquadBatch { Status = status, Settings = settings };
            batch.Queue.AddRange(queue);
            _squadBatch = batch;
            if (list == null)
                SetBatchStage(batch, SquadStage.FindList);
            else
                UseBatchList(batch, list);
            Log.LogInfo("RegenerateFM batch started for " + queue.Count + " faces (" +
                        queue.FindAll(candidate => candidate.Kind == ListKind.Players).Count + " players, " +
                        queue.FindAll(candidate => candidate.Kind == ListKind.Staff).Count + " staff)" +
                        (batch.ReturnByBack ? " (returning with Back)" : ""));
        }

        // The batch's list for its current kind, found: a squad page is returned
        // to as it was opened (Back from a team page), the staff table with Back.
        private static void UseBatchList(SquadBatch batch, ScrollView list)
        {
            batch.List = list;
            batch.SearchSteps = 0;
            batch.LastY = -1;
            if (batch.Kind == ListKind.Players)
            {
                batch.ReturnTab = SquadPageTeam(CurrentRoot());
                batch.ReturnByBack = !InSquadScreen(list);
            }
            list.scrollOffset = new Vector2(list.scrollOffset.x, 0);
            SetBatchStage(batch, SquadStage.FindPlayer);
        }

        // Between the batch's lists: a step each two seconds towards the next.
        private void FindBatchList(SquadBatch batch)
        {
            var list = ListScroll(batch.Kind, CurrentRoot());
            var now = System.DateTime.UtcNow;
            batch.Status.text = batch.Completed + " faces done. Opening " + OtherList(batch.Kind) + "…";
            if (now < batch.NextStepUtc)
                return;
            if (list != null)
            {
                UseBatchList(batch, list);
                return;
            }
            Log.LogInfo("Finding the club's " + Plural(batch.Kind) + ": " + StepToList(batch.Kind));
            batch.NextStepUtc = now.AddSeconds(2);
        }

        private void PollSquadBatch()
        {
            var batch = _squadBatch;
            if (batch == null)
                return;
            // FM26 is throttled while it isn't the focused app (macOS draws a hidden
            // window very slowly), so a profile then takes far longer to fill in.
            // Time away doesn't count towards the batch's waits.
            if (!Application.isFocused)
            {
                if (!batch.UnfocusedSinceUtc.HasValue)
                {
                    batch.UnfocusedSinceUtc = System.DateTime.UtcNow;
                    batch.Status.text = "Paused while FM26 is in the background; return to FM26 to continue.";
                    Log.LogInfo("RegenerateFM squad batch paused: FM26 is in the background");
                }
                return;
            }
            if (batch.UnfocusedSinceUtc.HasValue)
            {
                var away = System.DateTime.UtcNow - batch.UnfocusedSinceUtc.Value;
                batch.StageStartedUtc += away;
                batch.NextPlayerUtc += away;
                batch.UnfocusedSinceUtc = null;
                Log.LogInfo("RegenerateFM squad batch resumed after " + away.TotalSeconds.ToString("0") + " s in the background");
            }
            if (batch.StopAfterCurrent && batch.Stage != SquadStage.Request && batch.Stage != SquadStage.Refresh)
            {
                StopSquadBatch("Stopped after " + batch.Completed + (batch.Completed == 1 ? " face." : " faces.") + ScoutingNote(batch));
                return;
            }
            if (System.DateTime.UtcNow - batch.StageStartedUtc > TimeSpan.FromSeconds(45) &&
                batch.Stage != SquadStage.Request && batch.Stage != SquadStage.Refresh)
            {
                // FM26's Back history doesn't always hold the team page: after going
                // Back to it from Squad > Overview, Back from a profile opens the
                // Overview, and the team page can drop out of the history altogether.
                if (batch.Stage == SquadStage.AwaitSquad && batch.ReturnByBack)
                {
                    var team = batch.ReturnTab ?? "the team page";
                    StopSquadBatch("FM26's Back button didn't return to " + team + ", so the batch stopped after " +
                        batch.Completed + (batch.Completed == 1 ? " face" : " faces") + ". Open " + team +
                        "'s squad again and choose Generate faces… for the rest." + ScoutingNote(batch));
                }
                else
                    StopSquadBatch("Timed out while navigating FM26. " + batch.Completed + " faces were generated.");
                return;
            }
            switch (batch.Stage)
            {
                case SquadStage.FindList:
                    FindBatchList(batch);
                    break;
                case SquadStage.FindPlayer:
                    FindBatchPlayer(batch);
                    break;
                case SquadStage.AwaitProfile:
                    CaptureBatchPlayer(batch);
                    break;
                case SquadStage.Request:
                    PollBatchRequest(batch);
                    break;
                case SquadStage.Refresh:
                    if (_graphicsLoadSerial != batch.GraphicsSerial)
                    {
                        batch.Completed++;
                        batch.NextPlayerUtc = System.DateTime.UtcNow.AddSeconds(5);
                        SetBatchStage(batch, SquadStage.ReturnToSquad);
                    }
                    else if (!WaitingForGraphics)
                        StopSquadBatch("Graphics refresh failed after " + batch.Completed +
                            " completed faces. The last portrait was saved; restart FM26 to load it.");
                    break;
                case SquadStage.ReturnToSquad:
                    ReturnToSquad(batch);
                    break;
                case SquadStage.AwaitSquad:
                    var list = BatchList(batch);
                    if (list == null && batch.Kind == ListKind.Players && batch.ReturnTab != null && !batch.TabClicked)
                        OpenSquadTab(batch);
                    // Back from a profile can leave your own club's Staff page on another tab.
                    if (list == null && batch.Kind == ListKind.Staff && TopPage(CurrentRoot())?.name == "ClubAllStaffTool" &&
                        System.DateTime.UtcNow >= batch.NextStepUtc)
                    {
                        Log.LogInfo("Returning to the staff list: " + StepToList(ListKind.Staff));
                        batch.NextStepUtc = System.DateTime.UtcNow.AddSeconds(2);
                    }
                    if (list != null)
                    {
                        var kind = batch.Kind;
                        batch.Index++;
                        if (batch.Index >= batch.Queue.Count)
                            StopSquadBatch("Finished: " + Faces(batch.Completed) + " generated." + ScoutingNote(batch));
                        else if (batch.Kind != kind)
                        {
                            batch.NextStepUtc = System.DateTime.UtcNow;
                            SetBatchStage(batch, SquadStage.FindList);
                        }
                        else
                            UseBatchList(batch, list);
                    }
                    break;
            }
        }

        private static void SetBatchStage(SquadBatch batch, SquadStage stage)
        {
            batch.Stage = stage;
            batch.StageStartedUtc = System.DateTime.UtcNow;
        }

        private void FindBatchPlayer(SquadBatch batch)
        {
            if (System.DateTime.UtcNow < batch.NextPlayerUtc)
            {
                batch.Status.text = batch.Completed + " faces done. Next face in " +
                    Math.Ceiling((batch.NextPlayerUtc - System.DateTime.UtcNow).TotalSeconds) +
                    " seconds; you can stop the batch.";
                return;
            }
            if (BatchList(batch) == null || batch.List?.parent == null)
                throw new InvalidOperationException("The " + Plural(batch.Kind) + " list changed before finding " + batch.Queue[batch.Index].Name);
            var target = batch.Queue[batch.Index];
            batch.Status.text = "Finding " + target.Name + " (" + (batch.Index + 1) + "/" + batch.Queue.Count + ")…";
            var view = FindNamed(batch.List, "View");
            int readable = 0;
            for (int i = 0; view != null && i < view.hierarchy.childCount; i++)
            {
                var row = view.hierarchy.ElementAt(i);
                var candidate = ReadCandidate(target.Kind, row);
                if (candidate != null)
                    readable++;
                if (candidate == null || candidate.Key != target.Key)
                    continue;
                if (!IsDefaultPortrait(candidate.Address))
                {
                    Log.LogInfo("Skipping " + target.Name + ": portrait now exists");
                    SetBatchStage(batch, SquadStage.ReturnToSquad);
                    return;
                }
                if (target.Kind == ListKind.Staff)
                {
                    var name = FindText(row, target.Name);
                    if (name == null)
                        throw new InvalidOperationException("Staff name missing for " + target.Name);
                    // A click lands only on a row in view: the table lays out rows past its edge.
                    var shown = batch.List.contentViewport.worldBound;
                    if (name.worldBound.yMin < shown.yMin || name.worldBound.yMax > shown.yMax)
                    {
                        float most = Math.Max(0f, batch.List.contentContainer.layout.height - batch.List.contentViewport.layout.height);
                        float y = Math.Max(0f, Math.Min(most, batch.List.scrollOffset.y + name.worldBound.center.y - shown.center.y));
                        if (Math.Abs(y - batch.List.scrollOffset.y) > 1f)
                        {
                            batch.List.scrollOffset = new Vector2(batch.List.scrollOffset.x, y);
                            return;
                        }
                    }
                    SetBatchStage(batch, SquadStage.AwaitProfile);
                    PointerClick(name);
                    Log.LogInfo("Opened missing member of staff " + target.Name + " from the staff list");
                    return;
                }
                var button = FindNamed(row, "NameButton")?.TryCast<Button>();
                if (button == null)
                    throw new InvalidOperationException("Player link missing for " + target.Name);
                SetBatchStage(batch, SquadStage.AwaitProfile);
                button.clickable.SimulateSingleClick(null);
                Log.LogInfo("Opened missing player " + target.Name + " from squad row");
                return;
            }
            // A squad page that has just opened shows its rows before their names
            // (3-5 s on the Demo); wait for them rather than searching an empty list.
            if (readable == 0 && System.DateTime.UtcNow - batch.StageStartedUtc < TimeSpan.FromSeconds(20))
                return;
            float current = batch.List.scrollOffset.y;
            float max = Math.Max(0f, batch.List.contentContainer.layout.height -
                                   batch.List.contentViewport.layout.height);
            if (++batch.SearchSteps > 100)
                throw new InvalidOperationException("Could not find " + target.Name + " in the " + Plural(target.Kind) + " list");
            if (current >= max - 1f || (batch.LastY >= 0 && Math.Abs(current - batch.LastY) < 1f))
            {
                // A list that has just reopened can report no room to scroll before
                // its layout catches up, so sweep again from the top for a while.
                var searched = System.DateTime.UtcNow - batch.StageStartedUtc;
                if (searched >= TimeSpan.FromSeconds(15))
                    throw new InvalidOperationException("Could not find " + target.Name + " in the " + Plural(target.Kind) + " list (" +
                        readable + " rows readable, scroll " + current + " of " + max + ")");
                batch.LastY = -1;
                batch.List.scrollOffset = new Vector2(batch.List.scrollOffset.x, 0);
                return;
            }
            batch.LastY = current;
            batch.List.scrollOffset = new Vector2(batch.List.scrollOffset.x,
                Math.Min(max, current + Math.Max(60f, batch.List.contentViewport.layout.height * 0.35f)));
        }

        private void CaptureBatchPlayer(SquadBatch batch)
        {
            var target = batch.Queue[batch.Index];
            var header = FindNamed(CurrentRoot(), "ReportHeader");
            // A player-coach on the staff list opens as a player.
            bool asPlayer = IsPlayerOverview(CurrentRoot());
            if (header == null || !HeaderId(header, out long personId) || (target.Kind == ListKind.Players && !asPlayer))
            {
                // Not every name on the staff list is a link: clicking an owner's
                // only selects the row.
                if (target.Kind == ListKind.Staff && StaffScroll(CurrentRoot()) != null &&
                    System.DateTime.UtcNow - batch.StageStartedUtc > StaffOpenWait)
                {
                    Log.LogInfo("Skipping " + target.Name + ": their name on the staff list doesn't open a profile");
                    batch.Unopened.Add(target.Name);
                    SetBatchStage(batch, SquadStage.ReturnToSquad);
                }
                return;
            }
            ProfileSnapshot profile;
            VisualElement picture;
            VisualElement root;
            try { profile = asPlayer ? CaptureProfile(personId, out picture, out root) : CaptureStaff(personId, out picture, out root); }
            catch (NeedsScoutingException) when (System.DateTime.UtcNow - batch.StageStartedUtc >= ScoutingSettle)
            {
                // Hidden attributes, still hidden once the profile has settled: skip them.
                Log.LogInfo("Skipping " + target.Name + ": needs scouting first");
                batch.NeedsScouting++;
                SetBatchStage(batch, SquadStage.ReturnToSquad);
                return;
            }
            catch (InvalidOperationException) when (System.DateTime.UtcNow - batch.StageStartedUtc < ProfileBindTimeout)
            {
                // FM creates the header before its bound name, stats and height
                // arrive; under Rosetta 2 on macOS that took more than 10 s.
                return;
            }
            if (!string.Equals(profile.Name, target.Name, StringComparison.OrdinalIgnoreCase) ||
                (target.Age > 0 && profile.Age != target.Age))
            {
                if (System.DateTime.UtcNow - batch.StageStartedUtc < ProfileBindTimeout)
                    return;
                throw new InvalidOperationException("Opened profile does not match " + target.Name + "; no image requested");
            }
            var address = FindNamed(picture, "PersonImage")?.TryCast<SI.UI.Image>()?.m_imageAddressableID?.ToString();
            if (!IsDefaultPortrait(address))
            {
                Log.LogInfo("Skipping " + target.Name + ": profile portrait is not missing (" + address + ")");
                SetBatchStage(batch, SquadStage.ReturnToSquad);
                return;
            }
            // Gender arrives through its own binding; give it a moment, then go
            // without (the prompt then rules out facial hair).
            if (profile.Gender == null && System.DateTime.UtcNow - batch.StageStartedUtc < GenderWait)
                return;
            if (profile.BiographyHeadline == null && _biographyCache.TryGetValue(personId, out string biography))
                profile.BiographyHeadline = biography;
            // A silhouette is a real person, unless RegenerateFM saw them as a newgen.
            FindKnownEthnicity(personId, null, profile, out bool newgen);
            profile.Likeness = !newgen;
            batch.PersonId = personId;
            batch.Status.text = "Generating " + target.Name + " (" + (batch.Index + 1) + "/" + batch.Queue.Count + ")…";
            batch.Request = RequestPortrait(personId, profile, batch.Settings);
            SetBatchStage(batch, SquadStage.Request);
            Log.LogInfo((asPlayer ? "Player" : "Staff") + " portrait request started for " + target.Name + " (" + personId + ")");
        }

        private void PollBatchRequest(SquadBatch batch)
        {
            if (!batch.Request.IsCompleted)
            {
                if (System.DateTime.UtcNow - batch.StageStartedUtc > GenerationTimeout)
                    StopSquadBatch("Portrait request timed out for " + batch.Queue[batch.Index].Name);
                return;
            }
            var result = batch.Request.GetAwaiter().GetResult();
            if (!result.Success)
            {
                StopSquadBatch("Portrait request failed for " + batch.Queue[batch.Index].Name + ": " + result.Error);
                ShowKeyProblem(result);
                return;
            }
            Log.LogInfo("Squad portrait saved for " + batch.Queue[batch.Index].Name + " to " + result.GraphicsDir);
            batch.Status.text = "Loading " + batch.Queue[batch.Index].Name + " into FM26…";
            batch.GraphicsSerial = _graphicsLoadSerial;
            if (!Refresh())
            {
                StopSquadBatch("Portrait saved, but FM26 graphics refresh failed. Restart FM26 to load it.");
                return;
            }
            SetBatchStage(batch, SquadStage.Refresh);
        }

        // The team a TeamSquadReport page lists, its heading's first text, or null.
        private static string SquadPageTeam(VisualElement root)
        {
            var report = FindNamed(root, "TeamSquadReport");
            var table = report == null ? null : FindNamed(report, "playertable");
            var texts = table?.parent == null ? null : TextValues(table.parent);
            return texts != null && texts.Count > 0 ? texts[0] : null;
        }

        // Once Squad has opened the Overview (its SquadScreen, with no player table),
        // choose the batch's team in the squad navigation beside "Overview": the
        // team's own tab ("Atlanta United Academy"), or "First Team" for the club's.
        private void OpenSquadTab(SquadBatch batch)
        {
            var root = CurrentRoot();
            if (FindNamed(root, "SquadScreen") == null)
                return;
            var nav = FindText(root, "First Team");
            while (nav != null && FindText(nav, "Overview") == null)
                nav = nav.parent;
            if (nav == null)
                return;
            // English clubs label youth tabs by age ("Under 21s" for "Arsenal U21s").
            var age = Regex.Match(batch.ReturnTab ?? "", @"\bU(\d{2})s?$");
            var label = FindText(nav, batch.ReturnTab) ??
                        (age.Success ? FindText(nav, "Under " + age.Groups[1].Value + "s") : null) ??
                        FindText(nav, "First Team");
            var button = label;
            while (button != null && button.TryCast<Button>() == null)
                button = button.parent;
            var tab = button?.TryCast<Button>();
            if (tab == null)
                return;
            batch.TabClicked = true;
            tab.clickable.SimulateSingleClick(null);
            Log.LogInfo("Opened " + FirstText(label) + " for " + batch.ReturnTab + " from Squad Overview");
        }

        private void ReturnToSquad(SquadBatch batch)
        {
            batch.TabClicked = false;
            if (BatchList(batch) != null)
            {
                SetBatchStage(batch, SquadStage.AwaitSquad);
                return;
            }
            if (batch.ReturnByBack || batch.Kind == ListKind.Staff)
            {
                Button back = null;
                var named = new List<VisualElement>();
                CollectNamed(CurrentRoot(), "BackButton", named);
                foreach (var element in named)
                    if (back == null && element.worldBound.width > 0)
                        back = element.TryCast<Button>();
                if (back == null)
                    throw new InvalidOperationException("FM26's Back button was not found (" + named.Count + " named BackButton)");
                batch.TabClicked = true;
                SetBatchStage(batch, SquadStage.AwaitSquad);
                back.clickable.SimulateSingleClick(null);
                Log.LogInfo("Went back to " + (batch.Kind == ListKind.Staff ? "the staff list" : batch.ReturnTab) +
                            " after " + batch.Queue[batch.Index].Name);
                return;
            }
            var label = FindText(CurrentRoot(), "Squad");
            var button = label;
            while (button != null && button.TryCast<Button>() == null)
                button = button.parent;
            var nav = button?.TryCast<Button>();
            if (nav == null)
                throw new InvalidOperationException("Squad navigation button was not found");
            SetBatchStage(batch, SquadStage.AwaitSquad);
            nav.clickable.SimulateSingleClick(null);
            Log.LogInfo("Returned to Squad Overview after " + batch.Queue[batch.Index].Name);
        }

        // How many players the batch skipped because they need scouting, and the
        // staff it couldn't open, if any.
        private static string ScoutingNote(SquadBatch batch) =>
            (batch.NeedsScouting == 0 ? "" : " " + batch.NeedsScouting +
             (batch.NeedsScouting == 1 ? " player needs" : " players need") + " scouting before RegenerateFM can make their face.") +
            (batch.Unopened.Count == 0 ? "" : " Skipped " + string.Join(", ", batch.Unopened) +
             ": FM26's staff list doesn't link to " + (batch.Unopened.Count == 1 ? "their profile." : "their profiles."));

        private void StopSquadBatch(string message)
        {
            if (_squadBatch == null)
                return;
            Log.LogInfo("RegenerateFM squad batch: " + message);
            var status = _squadBatch.Status;
            _squadBatch = null;
            if (status != null && status.parent != null)
                status.text = message;
            // The batch's dialog becomes a message about where the batch ended (a
            // profile, after Stop), so leaving that page, not its start, closes it.
            _dialogPage = DialogPage(CurrentRoot());
            _dialogPageMisses = 0;
            var button = _squadDialog != null && _squadDialog.hierarchy.childCount > 1
                ? _squadDialog.hierarchy.ElementAt(1).TryCast<Button>() : null;
            if (button != null)
                button.text = "Close";
        }

        private void RequestStopSquadBatch()
        {
            var batch = _squadBatch;
            if (batch == null)
            {
                CancelSquadScan();
                return;
            }
            if (batch.Stage != SquadStage.Request && batch.Stage != SquadStage.Refresh)
            {
                StopSquadBatch("Stopped after " + batch.Completed + (batch.Completed == 1 ? " face." : " faces.") + ScoutingNote(batch));
                return;
            }
            batch.StopAfterCurrent = true;
            batch.Status.text = "Stopping after the current face finishes…";
            Log.LogInfo("RegenerateFM squad batch stop requested after " + batch.Completed + " completed faces");
        }

        private void CancelSquadScan()
        {
            if (_squadScan != null && _squadScan.List?.parent != null)
                _squadScan.List.scrollOffset = new Vector2(_squadScan.List.scrollOffset.x, _squadScan.OriginalY);
            _squadScan = null;
            if (_squadDialog != null && _squadDialog.parent != null)
                _squadDialog.RemoveFromHierarchy();
            _squadDialog = null;
        }
    }
}
