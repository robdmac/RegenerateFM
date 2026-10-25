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
    // The Squad menu's Generate faces…: scanning the squad and making the missing faces
    // one at a time.
    public sealed partial class Plugin
    {
        private sealed class SquadScan
        {
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
            public string Key;
            public string Name;
            public int Age;
            public string Address;
        }

        private enum SquadStage { FindPlayer, AwaitProfile, Request, Refresh, ReturnToSquad, AwaitSquad }

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
            public double LastDiagnosis = -10;
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
            var button = new Button((Il2CppSystem.Action)(System.Action)(() => StartSquadScan()))
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

        private void StartSquadScan()
        {
            if (_pending != null || WaitingForGraphics || _squadScan != null || _squadBatch != null)
            {
                Log.LogWarning("Squad scan cannot start while another RegenerateFM operation is active");
                return;
            }
            if (!KeyReady(StartSquadScan))
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
            var root = CurrentRoot();
            var table = SquadTable(root);
            var list = FindScroll(table);
            if (list == null)
            {
                Log.LogWarning("Squad player list was not found; no image request was made");
                return;
            }
            int expected = 0;
            foreach (var value in TextValues(table.parent))
            {
                // "Players (28)" on the first team, "U21 Squad Players (21)" on youth teams.
                var match = Regex.Match(value, @"^(?:Senior Squad |U\d{2} Squad |Squad )?Players\s*\((\d+)\)$");
                if (match.Success && int.TryParse(match.Groups[1].Value, out expected))
                    break;
            }
            // Without the count the scan still runs, but nothing can be generated.
            if (expected == 0)
            {
                var texts = TextValues(table.parent);
                Log.LogWarning("Squad player count was not found near the table; texts: " +
                    string.Join(" | ", texts.GetRange(0, Math.Min(40, texts.Count))));
            }
            var status = ShowDialog("Scanning squad portraits…", "Cancel", () => CancelSquadScan());
            _squadScan = new SquadScan
            { List = list, OriginalY = list.scrollOffset.y, Status = status, ExpectedCount = expected,
              TopReadyUtc = System.DateTime.UtcNow.AddSeconds(1) };
            list.scrollOffset = new Vector2(list.scrollOffset.x, 0);
            Log.LogInfo("RegenerateFM squad portrait scan started");
        }

        private void PollSquadScan()
        {
            var scan = _squadScan;
            if (scan == null)
                return;
            if (scan.List.parent == null || SquadTable(CurrentRoot()) == null)
            {
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
                var candidate = ReadSquadCandidate(row);
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
                scan.Status.text = "Scanning squad portraits… " + scan.Portraits.Count + " players checked";
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
                    Log.LogInfo("Squad scan read " + scan.Portraits.Count + " of " + scan.ExpectedCount +
                                " players; scanning again (pass " + scan.Passes + ")");
                    return;
                }
                scan.List.scrollOffset = new Vector2(scan.List.scrollOffset.x, scan.OriginalY);
                _squadScan = null;
                var kinds = new SortedSet<string>();
                foreach (var seen in scan.Portraits.Values)
                    if (seen.Address != null && !Regex.IsMatch(seen.Address, @"\d{4}"))
                        kinds.Add(seen.Address);
                Log.LogInfo("DIAG squad portrait kinds: " + string.Join(", ", kinds));
                Log.LogInfo("RegenerateFM squad scan finished: " + scan.Portraits.Count +
                            " of " + scan.ExpectedCount + " players, " + scan.Missing.Count + " missing portraits");
                if (scan.Status != null)
                    scan.Status.text = scan.ExpectedCount > 0 && scan.Portraits.Count != scan.ExpectedCount
                        ? "Squad scan incomplete (" + scan.Portraits.Count + " of " + scan.ExpectedCount +
                          " players). No images have been requested."
                        : scan.Missing.Count + " of " + scan.Portraits.Count +
                          " squad players have no portrait. No images have been requested.";
                if (scan.ExpectedCount > 0 && scan.Portraits.Count == scan.ExpectedCount && scan.Missing.Count > 0)
                {
                    var missing = new List<SquadCandidate>();
                    foreach (var key in scan.Missing)
                        missing.Add(scan.Portraits[key]);
                    missing.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    ShowSquadConfirmation(missing, scan.Portraits.Count);
                }
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

        private void ShowSquadConfirmation(List<SquadCandidate> missing, int squadCount)
        {
            var status = ShowDialog(missing.Count + " of " + squadCount +
                " players have no portrait. Generate " + missing.Count +
                " faces? Each face makes one billed API request.", "Cancel", () => CancelSquadScan());
            var generate = new Button((Il2CppSystem.Action)(System.Action)(() => StartSquadBatch(missing)))
            { text = "Generate " + missing.Count + " faces", focusable = false };
            generate.style.height = 34;
            generate.style.marginTop = 10;
            generate.style.color = Color.white;
            generate.style.backgroundColor = new Color(0.38f, 0.10f, 0.60f, 1f);
            _squadDialog.Insert(1, generate);
            Log.LogInfo("RegenerateFM squad confirmation ready: " + missing.Count + " missing portraits");
        }

        private void StartSquadBatch(List<SquadCandidate> missing)
        {
            if (missing.Count == 0 || _pending != null || WaitingForGraphics || _squadBatch != null)
                return;
            var list = FindScroll(SquadTable(CurrentRoot()));
            if (list == null)
            {
                Log.LogWarning("Squad list disappeared before generation; no image request was made");
                CancelSquadScan();
                return;
            }
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
            var status = ShowDialog("Opening the first missing player…", "Stop after current face",
                () => RequestStopSquadBatch());
            var batch = new SquadBatch { List = list, Status = status, Settings = settings,
                ReturnTab = SquadPageTeam(CurrentRoot()), ReturnByBack = !InSquadScreen(list) };
            batch.Queue.AddRange(missing);
            _squadBatch = batch;
            list.scrollOffset = new Vector2(list.scrollOffset.x, 0);
            Log.LogInfo("RegenerateFM squad batch started for " + missing.Count + " players" +
                        (batch.ReturnByBack ? " (returning with Back)" : ""));
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
                    if (list == null && batch.ReturnTab != null && !batch.TabClicked)
                        OpenSquadTab(batch);
                    if (list != null)
                    {
                        batch.Index++;
                        if (batch.Index >= batch.Queue.Count)
                            StopSquadBatch("Finished: " + batch.Completed + " squad faces generated." + ScoutingNote(batch));
                        else
                        {
                            batch.List = list;
                            batch.SearchSteps = 0;
                            batch.LastY = -1;
                            list.scrollOffset = new Vector2(list.scrollOffset.x, 0);
                            SetBatchStage(batch, SquadStage.FindPlayer);
                        }
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
                batch.Status.text = batch.Completed + " faces done. Next player in " +
                    Math.Ceiling((batch.NextPlayerUtc - System.DateTime.UtcNow).TotalSeconds) +
                    " seconds; you can stop the batch.";
                return;
            }
            if (BatchList(batch) == null || batch.List?.parent == null)
                throw new InvalidOperationException("Squad screen changed before finding " + batch.Queue[batch.Index].Name);
            var target = batch.Queue[batch.Index];
            batch.Status.text = "Finding " + target.Name + " (" + (batch.Index + 1) + "/" + batch.Queue.Count + ")…";
            var view = FindNamed(batch.List, "View");
            int readable = 0;
            for (int i = 0; view != null && i < view.hierarchy.childCount; i++)
            {
                var row = view.hierarchy.ElementAt(i);
                var candidate = ReadSquadCandidate(row);
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
                throw new InvalidOperationException("Could not find " + target.Name + " in the current squad");
            if (current >= max - 1f || (batch.LastY >= 0 && Math.Abs(current - batch.LastY) < 1f))
            {
                // A list that has just reopened can report no room to scroll before
                // its layout catches up, so sweep again from the top for a while.
                var searched = System.DateTime.UtcNow - batch.StageStartedUtc;
                if (searched >= TimeSpan.FromSeconds(15))
                    throw new InvalidOperationException("Could not find " + target.Name + " in the current squad (" +
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
            var header = FindNamed(CurrentRoot(), "ReportHeader");
            if (header == null || !HeaderId(header, out long personId) || !IsPlayerOverview(CurrentRoot()))
                return;
            var target = batch.Queue[batch.Index];
            ProfileSnapshot profile;
            VisualElement picture;
            VisualElement root;
            try { profile = CaptureProfile(personId, out picture, out root); }
            catch (NeedsScoutingException) when (System.DateTime.UtcNow - batch.StageStartedUtc >= ScoutingSettle)
            {
                // Hidden attributes, still hidden once the profile has settled: skip them.
                Log.LogInfo("Skipping " + target.Name + ": needs scouting first");
                batch.NeedsScouting++;
                SetBatchStage(batch, SquadStage.ReturnToSquad);
                return;
            }
            catch (InvalidOperationException error) when (System.DateTime.UtcNow - batch.StageStartedUtc < ProfileBindTimeout)
            {
                // FM creates the header before its bound name, stats and height
                // arrive; under Rosetta 2 on macOS that took more than 10 s.
                var waited = (System.DateTime.UtcNow - batch.StageStartedUtc).TotalSeconds;
                if (waited - batch.LastDiagnosis >= 2)
                {
                    batch.LastDiagnosis = waited;
                    var headers = new List<VisualElement>();
                    CollectNamed(CurrentRoot(), "ReportHeader", headers);
                    Log.LogInfo("DIAG batch capture after " + waited.ToString("0.0") + " s: " + headers.Count +
                                " ReportHeader(s), first " + header.worldBound + "; " + error.Message + " | " +
                                DescribeTexts(header));
                }
                return;
            }
            if (!string.Equals(profile.Name, target.Name, StringComparison.OrdinalIgnoreCase) ||
                (target.Age > 0 && profile.Age != target.Age))
            {
                if (System.DateTime.UtcNow - batch.StageStartedUtc < ProfileBindTimeout)
                    return;
                throw new InvalidOperationException("Opened player does not match " + target.Name + "; no image requested");
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
            Log.LogInfo("Squad portrait request started for " + target.Name + " (" + personId + ")");
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
            if (batch.ReturnByBack)
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
                Log.LogInfo("Went back to " + batch.ReturnTab + " after " + batch.Queue[batch.Index].Name);
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

        // How many players the batch skipped because they need scouting, if any.
        private static string ScoutingNote(SquadBatch batch) =>
            batch.NeedsScouting == 0 ? "" : " " + batch.NeedsScouting +
            (batch.NeedsScouting == 1 ? " player needs" : " players need") + " scouting before RegenerateFM can make their face.";

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
