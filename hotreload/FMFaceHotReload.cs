using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using FMActionLink;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SI.Bindable;
using SI.Core;
using SI.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace FMFaceHotReload
{
    [BepInPlugin("fmface.hotreload", "FM Face", "0.7.0")]
    [BepInDependency(Plugin.Guid)]
    public sealed class HotReloadPlugin : BasePlugin
    {
        internal static HotReloadPlugin Instance;
        internal static bool WaitingForGraphics;
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
        private static readonly Regex Tags = new Regex("<[^>]*>", RegexOptions.Compiled);
        private static readonly Regex AgePattern = new Regex(@"\b(\d{1,2}) years old\b", RegexOptions.Compiled);
        private static readonly Regex IdPattern = new Regex(@"\bID:\s*(\d{1,20})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StatPattern = new Regex(@"^(?:[1-9]|1[0-9]|20)(?:[-–](?:[1-9]|1[0-9]|20))?$", RegexOptions.Compiled);
        private static readonly Regex PositionPattern = new Regex(@"^(?:Goalkeeper|Defender|Wing[- ]Back|Defensive Midfielder|Midfielder|Attacking Midfielder|Striker)(?:\s*\([^)]{1,30}\))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> PhysicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Acceleration", "Agility", "Balance", "Jumping Reach", "Natural Fitness", "Pace", "Stamina", "Strength"
        };
        private static readonly HashSet<string> MentalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Aggression", "Anticipation", "Bravery", "Composure", "Concentration", "Decisions", "Determination",
            "Flair", "Leadership", "Off The Ball", "Positioning", "Teamwork", "Vision", "Work Rate"
        };
        private static readonly HashSet<string> GoalkeepingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Aerial Reach", "Command Of Area", "Communication", "Eccentricity", "Handling", "Kicking", "One On Ones",
            "Punching (Tendency)", "Reflexes", "Rushing Out (Tendency)", "Throwing"
        };
        private PendingGeneration _pending;
        private VisualElement _refreshOverlay;
        private Label _refreshMessage;
        private VisualElement _portraitMenu;
        private VisualElement _portraitDismissLayer;
        private SquadScan _squadScan;
        private VisualElement _squadDialog;
        private VisualElement _squadMenuButton;
        private VisualElement _squadMenuList;
        private SquadBatch _squadBatch;
        private int _graphicsLoadSerial;
        private long _portraitMenuPersonId;
        private System.DateTime _refreshStartedUtc;
        private readonly Dictionary<long, string> _biographyCache = new Dictionary<long, string>();
        private Harmony _harmony;
        private FaceMenuContributor _menuContributor;
        private static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan GraphicsTimeout = TimeSpan.FromSeconds(30);

        public override void Load()
        {
            if (!SupportedGameBuild())
            {
                Log.LogWarning("FM Face supports only the checked FM26 26.3.2 binaries; buttons disabled");
                return;
            }
            Instance = this;
            ClassInjector.RegisterTypeInIl2Cpp<FaceMenuContributor>(new RegisterTypeOptions
            {
                Interfaces = new[] { typeof(IMenuContributor) }
            });
            _menuContributor = new FaceMenuContributor();
            _harmony = new Harmony("fmface.hotreload");
            _harmony.PatchAll(typeof(AssetLoadPatch));
            _harmony.PatchAll(typeof(MenuOpenPatch));
            AddComponent<StaffWatcher>();
            Log.LogInfo("FM Face Actions and portrait menu integration loaded");
        }

        private bool SupportedGameBuild()
        {
            try
            {
                return HasHash(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"),
                           "7ce3eb474dc6093df633f979e869e55b2ec7953fde2e732392a694d379ff7a0c")
                    && HasHash(Path.Combine(Paths.GameRootPath, "fm_Data", "Plugins", "x86_64", "game_plugin.dll"),
                           "eb6c86fab56e051fe41482c7b93d8cb0af716a1cc2c6197ce1babbff68372bb8");
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not verify the FM26 build: " + error);
                return false;
            }
        }

        private static bool HasHash(string path, string expected)
        {
            if (!File.Exists(path))
                return false;
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return string.Equals(Convert.ToHexString(sha.ComputeHash(stream)), expected,
                                     StringComparison.OrdinalIgnoreCase);
        }

        private void Generate(long? personId, bool staff, bool update = false)
        {
            CheckRefreshTimeout();
            PollGeneration();
            if (_pending != null || _squadBatch != null)
            {
                Log.LogWarning("A portrait request is already in progress");
                ShowBusyNotice("Face generation is already in progress");
                return;
            }
            if (WaitingForGraphics)
            {
                Log.LogWarning("A portrait is still loading into FM26; wait for graphics refresh before generating again");
                ShowBusyNotice("Waiting for the previous face to load");
                return;
            }
            if (!personId.HasValue)
            {
                Log.LogWarning("Could not identify the current person; no image request was made");
                return;
            }
            VisualElement picture = null;
            VisualElement root = null;
            VisualElement overlay = null;
            Label message = null;
            try
            {
                var snapshot = staff ? CaptureStaff(personId.Value, out picture, out root)
                                     : CaptureProfile(personId.Value, out picture, out root);
                if (snapshot.BiographyHeadline == null && _biographyCache.TryGetValue(personId.Value, out string cachedBiography))
                    snapshot.BiographyHeadline = cachedBiography;
                string reference = null;
                if (update)
                {
                    if (!HasFace(picture))
                        throw new InvalidOperationException("This person has no face to update yet. Use Generate face instead.");
                    reference = CaptureReferencePng(picture);
                }
                overlay = BuildOverlay(picture, out message);
                Log.LogInfo((update ? "Updating face for " : "Generating face for ") + snapshot.Name + " (" + personId.Value + "): " +
                            snapshot.Physical.Count + " physical, " + snapshot.Technical.Count + " technical, " +
                            snapshot.Mental.Count + " mental, " + snapshot.Goalkeeping.Count + " goalkeeping attributes; bio=" +
                            (snapshot.BiographyHeadline != null));
                _pending = new PendingGeneration
                {
                    Task = RequestPortrait(personId.Value, snapshot, update ? "update" : "generate", reference),
                    Overlay = overlay,
                    Message = message,
                    StartedUtc = System.DateTime.UtcNow
                };
            }
            catch (Exception error)
            {
                _pending = null;
                Log.LogError("Could not read this person's profile: " + error);
                if (picture != null)
                {
                    if (overlay == null)
                        overlay = BuildOverlay(picture, out message);
                    ShowError(overlay, message, error.Message);
                }
            }
        }

        private sealed class PendingGeneration
        {
            public Task<GenerationResult> Task;
            public VisualElement Overlay;
            public Label Message;
            public System.DateTime StartedUtc;
        }

        private void PollGeneration()
        {
            var pending = _pending;
            if (pending == null)
                return;
            if (!pending.Task.IsCompleted)
            {
                if (System.DateTime.UtcNow - pending.StartedUtc < GenerationTimeout)
                    return;
                _pending = null;
                ShowError(pending.Overlay, pending.Message, "Portrait request timed out. Check the local generator before retrying.");
                Log.LogError("Face generation timed out; request lock released");
                return;
            }
            _pending = null;
            try
            {
                var result = pending.Task.GetAwaiter().GetResult();
                if (!result.Success)
                {
                    ShowError(pending.Overlay, pending.Message, result.Error);
                    Log.LogError("Face generation failed: " + result.Error);
                    return;
                }
                Log.LogInfo("Portrait saved to " + result.GraphicsDir);
                if (pending.Message != null)
                    pending.Message.text = "Loading face…";
                _refreshOverlay = pending.Overlay;
                _refreshMessage = pending.Message;
                if (!Refresh())
                {
                    ShowError(pending.Overlay, pending.Message,
                        "Portrait saved, but game refresh failed. Restart FM26 to load it.");
                    _refreshOverlay = null;
                    _refreshMessage = null;
                }
            }
            catch (Exception error)
            {
                ShowError(pending.Overlay, pending.Message, error.Message);
                Log.LogError("Face generation failed: " + error);
            }
        }

        private sealed class ProfileSnapshot
        {
            public string Type = "player";
            public string Name;
            public int Age;
            public string Nationality;
            public string JobTitle;
            public string Position;
            public string Club;
            public string Height;
            public string BiographyHeadline;
            public Dictionary<string, object> Physical = new Dictionary<string, object>();
            public Dictionary<string, object> Technical = new Dictionary<string, object>();
            public Dictionary<string, object> Mental = new Dictionary<string, object>();
            public Dictionary<string, object> Goalkeeping = new Dictionary<string, object>();
        }

        private static ProfileSnapshot CaptureProfile(long playerId, out VisualElement picture, out VisualElement root)
        {
            picture = null;
            root = null;
            var documents = Resources.FindObjectsOfTypeAll<UIDocument>();
            foreach (var document in documents)
                if (document.name == "PanelManager")
                    root = document.rootVisualElement;
            if (root == null)
                throw new InvalidOperationException("FM26 player UI was not found.");
            var header = FindNamed(root, "ReportHeader");
            if (header == null || header.worldBound.width < 800)
                throw new InvalidOperationException("Open a player Overview before generating a face.");
            if (!HeaderId(TextValues(header), out long visibleId))
                throw new InvalidOperationException("This player profile does not show its ID. Enable player IDs in the skin, then try again.");
            if (visibleId != playerId)
                throw new InvalidOperationException("Player profile changed; reopen it and try again.");
            picture = FindNamed(header, "PersonPicture");
            var banner = FindNamed(header, "report-player-banner-max");
            if (banner == null)
                banner = header;
            var profile = new ProfileSnapshot();
            var nameBox = FindNamed(banner, "Frame 1000003381");
            foreach (var value in TextValues(nameBox))
                if (!value.StartsWith("(ID:", StringComparison.OrdinalIgnoreCase))
                {
                    profile.Name = value;
                    break;
                }
            profile.Position = FirstText(FindNamed(banner, "Frame 1000003901"));
            profile.Club = FirstText(FindNamed(header, "PlayingTeam"));
            // "PlayingTeam" is the contract club; a loaned player's header also
            // shows the loan club, so the face wears the kit they play in now.
            var loanClub = FirstText(FindNamed(header, "TeamAwayFromOrTo"));
            if (!string.IsNullOrWhiteSpace(loanClub))
                profile.Club = loanClub;
            profile.Nationality = FirstText(FindNamed(header, "Frame 1000003766"));
            foreach (var value in TextValues(banner))
            {
                var match = AgePattern.Match(value);
                if (match.Success)
                {
                    profile.Age = int.Parse(match.Groups[1].Value);
                    break;
                }
            }
            var attributes = FindNamed(root, "PlayerAttributesBlock");
            if (attributes == null || attributes.worldBound.width < 500)
                throw new InvalidOperationException("The player Overview attribute panel is not visible.");
            var texts = TextValues(attributes);
            if (string.IsNullOrWhiteSpace(profile.Position))
                foreach (var value in texts)
                    if (PositionPattern.IsMatch(value))
                    {
                        profile.Position = value;
                        break;
                    }
            for (int i = 0; i + 1 < texts.Count; i++)
                if (texts[i] == "Height")
                {
                    profile.Height = texts[i + 1];
                    break;
                }
            foreach (var row in NamedElements(attributes, "attributes_row"))
            {
                if (row.worldBound.width < 40 || row.worldBound.height < 10)
                    continue;
                var values = TextValues(row);
                if (values.Count < 2)
                    continue;
                string stat = values[0];
                string score = values[values.Count - 1].Replace(" ", "");
                if (!StatPattern.IsMatch(score))
                    continue;
                object number = score.Contains("-") || score.Contains("–") ? score : (object)int.Parse(score);
                if (PhysicalNames.Contains(stat))
                    profile.Physical[stat] = number;
                else if (MentalNames.Contains(stat))
                    profile.Mental[stat] = number;
                else if (GoalkeepingNames.Contains(stat))
                    profile.Goalkeeping[stat] = number;
                else if (!stat.Contains("Rating"))
                    profile.Technical[stat] = number;
            }
            if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Position) ||
                string.IsNullOrWhiteSpace(profile.Club) || string.IsNullOrWhiteSpace(profile.Nationality) ||
                string.IsNullOrWhiteSpace(profile.Height) || profile.Age < 14 || profile.Physical.Count != 8 ||
                profile.Technical.Count + profile.Mental.Count + profile.Goalkeeping.Count < 6)
                throw new InvalidOperationException("Incomplete profile (no API request): " +
                    "name=" + profile.Name + ", age=" + profile.Age + ", nationality=" + profile.Nationality +
                    ", position=" + profile.Position + ", club=" + profile.Club + ", height=" + profile.Height +
                    ", physical=" + profile.Physical.Count + ", technical=" + profile.Technical.Count +
                    ", mental=" + profile.Mental.Count + ", goalkeeping=" + profile.Goalkeeping.Count + ".");
            profile.BiographyHeadline = CaptureBiographyHeadline(root);
            return profile;
        }

        private static ProfileSnapshot CaptureStaff(long personId, out VisualElement picture, out VisualElement root)
        {
            picture = null;
            root = CurrentRoot();
            if (root == null)
                throw new InvalidOperationException("FM26 staff UI was not found.");
            var header = FindNamed(root, "ReportHeader");
            if (header == null || header.worldBound.width < 800 || IsPlayerOverview(root) || IsPlayerHeader(header))
                throw new InvalidOperationException("Open a staff Overview before generating a face.");
            picture = FindNamed(header, "PersonPicture");
            var texts = TextValues(header);
            if (!HeaderId(texts, out long visibleId) || visibleId != personId)
                throw new InvalidOperationException("Staff profile changed; reopen it and try again.");
            var profile = new ProfileSnapshot { Type = "staff" };
            var banner = FindNamed(header, "report-player-banner-max") ?? header;
            var bannerTexts = TextValues(banner);
            for (int i = 0; i < bannerTexts.Count; i++)
            {
                if (IdPattern.IsMatch(bannerTexts[i]))
                {
                    profile.Name = IdPattern.Replace(bannerTexts[i], "").Trim();
                    profile.Name = profile.Name.Trim(' ', '(', ')');
                    if (profile.Name.Length == 0 && i > 0)
                        profile.Name = bannerTexts[i - 1];
                    if (i + 1 < bannerTexts.Count)
                        profile.JobTitle = bannerTexts[i + 1];
                    break;
                }
            }
            profile.JobTitle = FirstText(FindNamed(banner, "Frame 1000003901")) ?? profile.JobTitle;
            profile.Club = FirstText(FindNamed(header, "PlayingTeam"));
            profile.Nationality = FirstText(FindNamed(header, "Frame 1000003766"));
            foreach (var value in bannerTexts)
            {
                var match = AgePattern.Match(value);
                if (match.Success)
                {
                    profile.Age = int.Parse(match.Groups[1].Value);
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(profile.Nationality))
            {
                var ageIndex = bannerTexts.FindIndex(value => AgePattern.IsMatch(value));
                if (ageIndex > 0)
                    profile.Nationality = bannerTexts[ageIndex - 1];
            }
            if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.JobTitle) ||
                string.IsNullOrWhiteSpace(profile.Nationality) || profile.Age < 14 || profile.Age > 100 || picture == null)
                throw new InvalidOperationException("Incomplete staff profile (no API request): name=" + profile.Name +
                    ", age=" + profile.Age + ", nationality=" + profile.Nationality + ", job=" + profile.JobTitle + ".");
            profile.BiographyHeadline = CaptureBiographyHeadline(root);
            return profile;
        }

        private static VisualElement CurrentRoot()
        {
            foreach (var document in Resources.FindObjectsOfTypeAll<UIDocument>())
                if (document.name == "PanelManager")
                    return document.rootVisualElement;
            return null;
        }

        private static bool IsPlayerOverview(VisualElement root)
        {
            var attributes = FindNamed(root, "PlayerAttributesBlock");
            return attributes != null && attributes.worldBound.width > 500;
        }

        private static bool IsPlayerHeader(VisualElement header)
        {
            foreach (var value in TextValues(header))
                if (PositionPattern.IsMatch(value))
                    return true;
            return false;
        }

        private static bool HeaderId(List<string> values, out long id)
        {
            foreach (var value in values)
            {
                var match = IdPattern.Match(value);
                if (match.Success && long.TryParse(match.Groups[1].Value, out id))
                    return true;
            }
            id = 0;
            return false;
        }

        private static string CaptureBiographyHeadline(VisualElement root)
        {
            foreach (var section in NamedElements(root, "Biography"))
            {
                var values = TextValues(section);
                foreach (var value in values)
                    if (!value.Equals("Biography", StringComparison.OrdinalIgnoreCase) && value.Length >= 35)
                        return FirstParagraph(value);
            }
            var visible = TextValues(root);
            for (int i = 0; i < visible.Count; i++)
                if (visible[i].Equals("Biography", StringComparison.OrdinalIgnoreCase))
                    for (int j = i + 1; j < Math.Min(i + 8, visible.Count); j++)
                    {
                        if (visible[j].Length >= 35 && visible[j].Contains(" "))
                            return FirstParagraph(visible[j]);
                        if (visible[j].Equals("Staff History", StringComparison.OrdinalIgnoreCase))
                            break;
                    }
            return null;
        }

        private static string FirstParagraph(string text)
        {
            var paragraph = Regex.Split(text.Trim(), @"\r?\n\s*\r?\n")[0].Trim();
            return paragraph.Length <= 600 ? paragraph : paragraph.Substring(0, 600).TrimEnd();
        }

        public sealed class StaffWatcher : MonoBehaviour
        {
            private float _nextScan;

            public StaffWatcher(IntPtr pointer) : base(pointer) { }

            private void Update()
            {
                var plugin = Instance;
                if (plugin == null)
                    return;
                plugin.CheckRefreshTimeout();
                plugin.PollGeneration();
                if (Time.unscaledTime < _nextScan)
                    return;
                _nextScan = Time.unscaledTime + 0.5f;
                try { plugin.CheckBiographyCache(); }
                catch (Exception error) { plugin.Log.LogWarning("Biography scan failed: " + error.Message); }
                try { plugin.CheckPortraitTrigger(); }
                catch (Exception error) { plugin.Log.LogWarning("Portrait menu setup failed: " + error.Message); }
                try { plugin.CheckSquadMenu(); plugin.PollSquadScan(); }
                catch (Exception error) { plugin.Log.LogWarning("Squad scan failed: " + error); plugin.CancelSquadScan(); }
                try { plugin.PollSquadBatch(); }
                catch (Exception error) { plugin.StopSquadBatch("Squad generation stopped: " + error.Message); }
            }
        }

        private sealed class SquadScan
        {
            public ScrollView List;
            public float OriginalY;
            public System.DateTime TopReadyUtc;
            public bool AwaitTop = true;
            public float LastY = -1;
            public int Steps;
            public int ExpectedCount;
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
            public long PersonId;
            public int GraphicsSerial;
            public Label Status;
            public bool StopAfterCurrent;
            public System.DateTime NextPlayerUtc;
        }

        private void CheckSquadMenu()
        {
            var root = CurrentRoot();
            if (FindNamed(root, "SquadScreen") == null || FindNamed(root, "PlayerTableContainer") == null)
            {
                RemoveSquadMenuButton();
                return;
            }
            // The top Squad navigation dropdown is a floating, virtualized View.
            // "Squad Numbers" uniquely identifies this menu on the checked build.
            var menuItem = FindText(root, "Squad Numbers");
            var menuList = menuItem;
            while (menuList != null && menuList.name != "View")
                menuList = menuList.parent;
            if (menuList == null || menuList.worldBound.width < 100)
            {
                RemoveSquadMenuButton();
                return;
            }
            var scroll = menuList.parent?.TryCast<ScrollView>();
            if (scroll == null)
                return;
            if (_squadMenuButton != null && _squadMenuButton.parent != null && _squadMenuList == menuList)
            {
                PositionSquadMenuButton(_squadMenuButton, scroll, root);
                return;
            }
            RemoveSquadMenuButton();
            var button = new Button((Il2CppSystem.Action)(System.Action)(() => StartSquadScan()))
            {
                name = "fmface-squad-command", text = "Generate faces…", focusable = false
            };
            button.style.height = 36;
            button.style.position = Position.Absolute;
            PositionSquadMenuButton(button, scroll, root);
            button.style.color = Color.white;
            button.style.backgroundColor = new Color(0.18f, 0.13f, 0.24f, 1f);
            button.style.unityTextAlign = TextAnchor.MiddleLeft;
            button.style.paddingLeft = 12;
            root.Add(button);
            _squadMenuButton = button;
            _squadMenuList = menuList;
            Log.LogInfo("FM Face squad command added to Squad menu");
        }

        private static void PositionSquadMenuButton(VisualElement button, ScrollView scroll, VisualElement root)
        {
            button.style.width = scroll.worldBound.width;
            button.style.left = scroll.worldBound.x - root.worldBound.x;
            button.style.top = scroll.worldBound.yMax - root.worldBound.y + 2;
        }

        private void RemoveSquadMenuButton()
        {
            if (_squadMenuButton != null && _squadMenuButton.parent != null)
                _squadMenuButton.RemoveFromHierarchy();
            _squadMenuButton = null;
            _squadMenuList = null;
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
                Log.LogWarning("Squad scan cannot start while another FM Face operation is active");
                return;
            }
            var root = CurrentRoot();
            var table = FindNamed(root, "PlayerTableContainer");
            var list = FindScroll(table);
            if (list == null)
            {
                Log.LogWarning("Squad player list was not found; no image request was made");
                return;
            }
            int expected = 0;
            foreach (var value in TextValues(table.parent))
            {
                var match = Regex.Match(value, @"^Players\s*\((\d+)\)$");
                if (match.Success && int.TryParse(match.Groups[1].Value, out expected))
                    break;
            }
            var status = ShowSquadDialog("Scanning squad portraits…", "Cancel", () => CancelSquadScan());
            _squadScan = new SquadScan
            { List = list, OriginalY = list.scrollOffset.y, Status = status, ExpectedCount = expected,
              TopReadyUtc = System.DateTime.UtcNow.AddSeconds(1) };
            list.scrollOffset = new Vector2(list.scrollOffset.x, 0);
            Log.LogInfo("FM Face squad portrait scan started");
        }

        private void PollSquadScan()
        {
            var scan = _squadScan;
            if (scan == null)
                return;
            if (scan.List.parent == null || FindNamed(CurrentRoot(), "SquadScreen") == null)
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
                if (candidate.Address == "person_default")
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
                scan.List.scrollOffset = new Vector2(scan.List.scrollOffset.x, scan.OriginalY);
                _squadScan = null;
                Log.LogInfo("FM Face squad scan finished: " + scan.Portraits.Count +
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
            var values = TextValues(row);
            var position = values.Count > 1 ? values[1] : "";
            int.TryParse(values.Count > 2 ? values[2] : "", out int age);
            return new SquadCandidate
            {
                Key = name + "|" + position + "|" + age,
                Name = name, Age = age, Address = image.m_imageAddressableID?.ToString() ?? ""
            };
        }

        private void ShowSquadConfirmation(List<SquadCandidate> missing, int squadCount)
        {
            var status = ShowSquadDialog(missing.Count + " of " + squadCount +
                " players have no portrait. Generate " + missing.Count +
                " faces? Each face makes one billed API request.", "Cancel", () => CancelSquadScan());
            var generate = new Button((Il2CppSystem.Action)(System.Action)(() => StartSquadBatch(missing)))
            { text = "Generate " + missing.Count + " faces", focusable = false };
            generate.style.height = 34;
            generate.style.marginTop = 10;
            generate.style.color = Color.white;
            generate.style.backgroundColor = new Color(0.38f, 0.10f, 0.60f, 1f);
            _squadDialog.Insert(1, generate);
            Log.LogInfo("FM Face squad confirmation ready: " + missing.Count + " missing portraits");
        }

        private void StartSquadBatch(List<SquadCandidate> missing)
        {
            if (missing.Count == 0 || _pending != null || WaitingForGraphics || _squadBatch != null)
                return;
            var list = FindScroll(FindNamed(CurrentRoot(), "PlayerTableContainer"));
            if (list == null)
            {
                Log.LogWarning("Squad list disappeared before generation; no image request was made");
                CancelSquadScan();
                return;
            }
            var status = ShowSquadDialog("Opening the first missing player…", "Stop after current face",
                () => RequestStopSquadBatch());
            var batch = new SquadBatch { List = list, Status = status };
            batch.Queue.AddRange(missing);
            _squadBatch = batch;
            list.scrollOffset = new Vector2(list.scrollOffset.x, 0);
            Log.LogInfo("FM Face squad batch started for " + missing.Count + " players");
        }

        private void PollSquadBatch()
        {
            var batch = _squadBatch;
            if (batch == null)
                return;
            if (batch.StopAfterCurrent && batch.Stage != SquadStage.Request && batch.Stage != SquadStage.Refresh)
            {
                StopSquadBatch("Stopped after " + batch.Completed + " faces.");
                return;
            }
            if (System.DateTime.UtcNow - batch.StageStartedUtc > TimeSpan.FromSeconds(45) &&
                batch.Stage != SquadStage.Request && batch.Stage != SquadStage.Refresh)
            {
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
                    var list = FindScroll(FindNamed(CurrentRoot(), "PlayerTableContainer"));
                    if (FindNamed(CurrentRoot(), "SquadScreen") != null && list != null)
                    {
                        batch.Index++;
                        if (batch.Index >= batch.Queue.Count)
                            StopSquadBatch("Finished: " + batch.Completed + " squad faces generated.");
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
            if (FindNamed(CurrentRoot(), "SquadScreen") == null || batch.List?.parent == null)
                throw new InvalidOperationException("Squad screen changed before finding " + batch.Queue[batch.Index].Name);
            var target = batch.Queue[batch.Index];
            batch.Status.text = "Finding " + target.Name + " (" + (batch.Index + 1) + "/" + batch.Queue.Count + ")…";
            var view = FindNamed(batch.List, "View");
            for (int i = 0; view != null && i < view.hierarchy.childCount; i++)
            {
                var row = view.hierarchy.ElementAt(i);
                var candidate = ReadSquadCandidate(row);
                if (candidate == null || candidate.Key != target.Key)
                    continue;
                if (candidate.Address != "person_default")
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
            float current = batch.List.scrollOffset.y;
            float max = Math.Max(0f, batch.List.contentContainer.layout.height -
                                   batch.List.contentViewport.layout.height);
            if (++batch.SearchSteps > 100 || current >= max - 1f ||
                (batch.LastY >= 0 && Math.Abs(current - batch.LastY) < 1f))
                throw new InvalidOperationException("Could not find " + target.Name + " in the current squad");
            batch.LastY = current;
            batch.List.scrollOffset = new Vector2(batch.List.scrollOffset.x,
                Math.Min(max, current + Math.Max(60f, batch.List.contentViewport.layout.height * 0.35f)));
        }

        private void CaptureBatchPlayer(SquadBatch batch)
        {
            var header = FindNamed(CurrentRoot(), "ReportHeader");
            if (header == null || !HeaderId(TextValues(header), out long personId) || !IsPlayerOverview(CurrentRoot()))
                return;
            var target = batch.Queue[batch.Index];
            ProfileSnapshot profile;
            VisualElement picture;
            VisualElement root;
            try { profile = CaptureProfile(personId, out picture, out root); }
            catch (InvalidOperationException) when (System.DateTime.UtcNow - batch.StageStartedUtc < TimeSpan.FromSeconds(10))
            {
                // FM creates the header before its bound name, stats and height arrive.
                return;
            }
            if (!string.Equals(profile.Name, target.Name, StringComparison.OrdinalIgnoreCase) ||
                (target.Age > 0 && profile.Age != target.Age))
            {
                if (System.DateTime.UtcNow - batch.StageStartedUtc < TimeSpan.FromSeconds(10))
                    return;
                throw new InvalidOperationException("Opened player does not match " + target.Name + "; no image requested");
            }
            var address = FindNamed(picture, "PersonImage")?.TryCast<SI.UI.Image>()?.m_imageAddressableID?.ToString();
            if (address != "person_default")
            {
                Log.LogInfo("Skipping " + target.Name + ": profile portrait is not missing (" + address + ")");
                SetBatchStage(batch, SquadStage.ReturnToSquad);
                return;
            }
            if (profile.BiographyHeadline == null && _biographyCache.TryGetValue(personId, out string biography))
                profile.BiographyHeadline = biography;
            batch.PersonId = personId;
            batch.Status.text = "Generating " + target.Name + " (" + (batch.Index + 1) + "/" + batch.Queue.Count + ")…";
            batch.Request = RequestPortrait(personId, profile);
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

        private void ReturnToSquad(SquadBatch batch)
        {
            if (FindNamed(CurrentRoot(), "SquadScreen") != null &&
                FindNamed(CurrentRoot(), "PlayerTableContainer") != null)
            {
                SetBatchStage(batch, SquadStage.AwaitSquad);
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

        private void StopSquadBatch(string message)
        {
            if (_squadBatch == null)
                return;
            Log.LogInfo("FM Face squad batch: " + message);
            var status = _squadBatch.Status;
            _squadBatch = null;
            if (status != null && status.parent != null)
                status.text = message;
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
                StopSquadBatch("Stopped after " + batch.Completed + " faces.");
                return;
            }
            batch.StopAfterCurrent = true;
            batch.Status.text = "Stopping after the current face finishes…";
            Log.LogInfo("FM Face squad batch stop requested after " + batch.Completed + " completed faces");
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

        private Label ShowSquadDialog(string text, string buttonText, Action buttonAction)
        {
            CancelSquadScan();
            var root = CurrentRoot();
            var dialog = new VisualElement { name = "fmface-squad-dialog" };
            dialog.style.position = Position.Absolute;
            dialog.style.right = 24;
            dialog.style.top = 80;
            dialog.style.width = 360;
            dialog.style.paddingTop = 16;
            dialog.style.paddingBottom = 16;
            dialog.style.paddingLeft = 16;
            dialog.style.paddingRight = 16;
            dialog.style.backgroundColor = new Color(0.13f, 0.10f, 0.18f, 0.97f);
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.style.color = Color.white;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 16;
            dialog.Add(label);
            var button = new Button((Il2CppSystem.Action)(System.Action)(buttonAction))
            { text = buttonText, focusable = false };
            button.style.marginTop = 12;
            button.style.height = 32;
            dialog.Add(button);
            root.Add(dialog);
            _squadDialog = dialog;
            return label;
        }

        private void CheckPortraitTrigger()
        {
            var root = CurrentRoot();
            var header = FindNamed(root, "ReportHeader");
            if (header == null || header.worldBound.width < 800 ||
                !HeaderId(TextValues(header), out long personId))
            {
                ClosePortraitMenu();
                return;
            }
            var picture = FindNamed(header, "PersonPicture");
            if (picture == null || picture.worldBound.width < 30 || picture.worldBound.height < 30)
            {
                ClosePortraitMenu();
                return;
            }
            if (_portraitMenu != null && (_portraitMenu.parent == null || _portraitMenuPersonId != personId))
                ClosePortraitMenu();
            if (picture.Q<VisualElement>("fmface-portrait-trigger") != null)
                return;
            var trigger = new Button((Il2CppSystem.Action)(System.Action)(() => ShowPortraitMenu()))
            {
                name = "fmface-portrait-trigger",
                text = "",
                focusable = false
            };
            trigger.style.position = Position.Absolute;
            trigger.style.left = 0;
            trigger.style.top = 0;
            trigger.style.width = Length.Percent(100);
            trigger.style.height = Length.Percent(100);
            trigger.style.opacity = 0.01f;
            picture.Add(trigger);
            Log.LogInfo("FM Face portrait click enabled for " + personId);
        }

        private void ShowPortraitMenu()
        {
            var root = CurrentRoot();
            var header = FindNamed(root, "ReportHeader");
            var picture = FindNamed(header, "PersonPicture");
            if (root == null || header == null || picture == null ||
                !HeaderId(TextValues(header), out long personId))
                return;
            if (_portraitMenu != null && _portraitMenu.parent != null && _portraitMenuPersonId == personId)
            {
                _portraitMenu.BringToFront();
                return;
            }
            ClosePortraitMenu();
            // A full-panel hit target keeps clicks outside the menu from reaching
            // either the menu action or controls behind it. The menu is added last.
            var dismissLayer = new Button((Il2CppSystem.Action)(System.Action)(() => ClosePortraitMenu()))
            {
                name = "fmface-portrait-dismiss",
                text = "",
                focusable = false
            };
            dismissLayer.style.position = Position.Absolute;
            dismissLayer.style.left = 0;
            dismissLayer.style.top = 0;
            dismissLayer.style.width = Length.Percent(100);
            dismissLayer.style.height = Length.Percent(100);
            dismissLayer.style.opacity = 0f;
            root.Add(dismissLayer);
            var menu = new VisualElement { name = "fmface-portrait-menu" };
            menu.style.position = Position.Absolute;
            menu.style.left = Math.Max(0f, picture.worldBound.x - root.worldBound.x);
            menu.style.top = Math.Max(0f, picture.worldBound.yMax - root.worldBound.y + 3f);
            menu.style.width = 190;
            menu.style.height = 66;
            menu.style.backgroundColor = new Color(0.16f, 0.16f, 0.16f, 1f);
            menu.style.borderTopWidth = 1;
            menu.style.borderBottomWidth = 1;
            menu.style.borderLeftWidth = 1;
            menu.style.borderRightWidth = 1;
            menu.style.borderTopColor = Color.gray;
            menu.style.borderBottomColor = Color.gray;
            menu.style.borderLeftColor = Color.gray;
            menu.style.borderRightColor = Color.gray;
            bool hasFace = HasFace(picture);
            menu.Add(PortraitMenuItem("fmface-portrait-generate", "Generate new face", personId, false, true));
            menu.Add(PortraitMenuItem("fmface-portrait-update", "Update face", personId, true, hasFace));
            root.Add(menu);
            _portraitDismissLayer = dismissLayer;
            _portraitMenu = menu;
            _portraitMenuPersonId = personId;
        }

        private Button PortraitMenuItem(string name, string text, long personId, bool update, bool enabled)
        {
            var item = new Button((Il2CppSystem.Action)(System.Action)(() =>
            {
                ClosePortraitMenu();
                var currentRoot = CurrentRoot();
                var currentHeader = FindNamed(currentRoot, "ReportHeader");
                if (currentHeader == null || !HeaderId(TextValues(currentHeader), out long currentId) ||
                    currentId != personId)
                {
                    Log.LogWarning("Portrait menu profile changed before selection; no image request was made");
                    return;
                }
                bool staff = !IsPlayerOverview(currentRoot) && !IsPlayerHeader(currentHeader);
                Generate(currentId, staff, update);
            })) { name = name, text = text };
            item.style.width = Length.Percent(100);
            item.style.height = 32;
            item.style.color = enabled ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);
            item.style.backgroundColor = new Color(0.19f, 0.15f, 0.25f, 1f);
            item.style.unityTextAlign = TextAnchor.MiddleLeft;
            item.style.paddingLeft = 10;
            item.SetEnabled(enabled);
            if (!enabled)
                item.tooltip = "No face to update yet";
            return item;
        }

        private void ClosePortraitMenu()
        {
            if (_portraitMenu != null && _portraitMenu.parent != null)
                _portraitMenu.RemoveFromHierarchy();
            if (_portraitDismissLayer != null && _portraitDismissLayer.parent != null)
                _portraitDismissLayer.RemoveFromHierarchy();
            _portraitMenu = null;
            _portraitDismissLayer = null;
        }

        private void CheckBiographyCache()
        {
            var root = CurrentRoot();
            if (root == null)
                return;
            var header = FindNamed(root, "ReportHeader");
            if (header == null || header.worldBound.width < 800 || FindNamed(header, "PersonPicture") == null)
                return;
            if (!HeaderId(TextValues(header), out long personId))
                return;
            var biography = CaptureBiographyHeadline(root);
            if (!string.IsNullOrWhiteSpace(biography))
                _biographyCache[personId] = biography;
        }

        private static VisualElement FindText(VisualElement element, string value)
        {
            if (element == null || element.worldBound.width <= 0 || element.worldBound.height <= 0)
                return null;
            var text = element.TryCast<TextElement>();
            if (text != null && WebUtility.HtmlDecode(Tags.Replace(text.text ?? "", "")).Trim() == value)
                return element;
            for (int i = 0; i < element.hierarchy.childCount; i++)
            {
                var found = FindText(element.hierarchy.ElementAt(i), value);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static VisualElement FindNamed(VisualElement element, string name)
        {
            if (element == null)
                return null;
            if (element.name == name && element.worldBound.width > 0 && element.worldBound.height > 0)
                return element;
            for (int i = 0; i < element.hierarchy.childCount; i++)
            {
                var found = FindNamed(element.hierarchy.ElementAt(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static List<VisualElement> NamedElements(VisualElement element, string name)
        {
            var result = new List<VisualElement>();
            CollectNamed(element, name, result);
            return result;
        }

        private static void CollectNamed(VisualElement element, string name, List<VisualElement> result)
        {
            if (element == null)
                return;
            if (element.name != null && element.name.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                result.Add(element);
            for (int i = 0; i < element.hierarchy.childCount; i++)
                CollectNamed(element.hierarchy.ElementAt(i), name, result);
        }

        private static List<string> TextValues(VisualElement element)
        {
            var values = new List<string>();
            CollectTexts(element, values);
            return values;
        }

        private static void CollectTexts(VisualElement element, List<string> values)
        {
            if (element == null || element.worldBound.width <= 0 || element.worldBound.height <= 0)
                return;
            var text = element.TryCast<TextElement>();
            if (text != null)
            {
                var value = WebUtility.HtmlDecode(Tags.Replace(text.text ?? "", "")).Trim();
                if (value.Length > 0)
                    values.Add(value);
            }
            for (int i = 0; i < element.hierarchy.childCount; i++)
                CollectTexts(element.hierarchy.ElementAt(i), values);
        }

        private static string FirstText(VisualElement element)
        {
            var values = TextValues(element);
            return values.Count > 0 ? values[0] : null;
        }

        private void ShowBusyNotice(string text)
        {
            try
            {
                var picture = FindNamed(FindNamed(CurrentRoot(), "ReportHeader"), "PersonPicture");
                if (picture == null)
                    return;
                var previous = picture.Q<VisualElement>("fmface-busy-notice");
                if (previous != null)
                    previous.RemoveFromHierarchy();
                var notice = new VisualElement { name = "fmface-busy-notice", pickingMode = PickingMode.Ignore };
                notice.style.position = Position.Absolute;
                notice.style.left = 0;
                notice.style.top = 0;
                notice.style.width = Length.Percent(100);
                notice.style.height = Length.Percent(100);
                notice.style.backgroundColor = new Color(0.16f, 0.08f, 0.27f, 0.9f);
                notice.style.justifyContent = Justify.Center;
                notice.style.alignItems = Align.Center;
                var label = new Label(text) { pickingMode = PickingMode.Ignore };
                label.style.color = Color.white;
                label.style.fontSize = 14;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.width = Length.Percent(95);
                notice.Add(label);
                picture.Add(notice);
                notice.schedule.Execute((Il2CppSystem.Action)(System.Action)(() =>
                {
                    if (notice.parent != null)
                        notice.RemoveFromHierarchy();
                })).StartingIn(2500);
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not show face-generation status: " + error.Message);
            }
        }

        // FM26 shows "person_default" (a silhouette) when a person has no face:
        // neither a generated one nor a game photo.
        private static bool HasFace(VisualElement picture)
        {
            var image = picture == null ? null : FindNamed(picture, "PersonImage")?.TryCast<SI.UI.Image>();
            var address = image?.m_imageAddressableID?.ToString();
            return !string.IsNullOrEmpty(address) && address != "person_default";
        }

        // The face FM26 is showing, as base64 PNG, for "Update face". The local
        // generator prefers its own full-size file when this is a generated face.
        private string CaptureReferencePng(VisualElement picture)
        {
            var image = FindNamed(picture, "PersonImage")?.TryCast<SI.UI.Image>();
            var sprite = image?.sprite;
            Texture source = sprite != null ? sprite.texture : image?.m_createdTexture;
            if (source == null)
                source = image?.image;
            if (source == null)
            {
                Log.LogWarning("The current portrait texture was not found");
                return null;
            }
            var rect = sprite != null ? sprite.textureRect : new Rect(0, 0, source.width, source.height);
            int width = Mathf.RoundToInt(rect.width), height = Mathf.RoundToInt(rect.height);
            if (width < 16 || height < 16 || width > 4096 || height > 4096)
            {
                Log.LogWarning("The current portrait texture has an unexpected size: " + width + "x" + height);
                return null;
            }
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                Graphics.Blit(source, target, new Vector2(rect.width / source.width, rect.height / source.height),
                              new Vector2(rect.x / source.width, rect.y / source.height));
                RenderTexture.active = target;
                copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply();
                // ImageConversion.EncodeToPNG fails in the IL2CPP interop layer,
                // so read the pixels back and encode the PNG here.
                var pixels = copy.GetPixels32();
                var rgba = new byte[width * height * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i];
                    rgba[i * 4] = pixel.r;
                    rgba[i * 4 + 1] = pixel.g;
                    rgba[i * 4 + 2] = pixel.b;
                    rgba[i * 4 + 3] = pixel.a;
                }
                Log.LogInfo("Captured the current portrait (" + width + "x" + height + ")");
                return Convert.ToBase64String(EncodePng(rgba, width, height));
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not capture the current portrait: " + error);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (copy != null)
                    UnityEngine.Object.Destroy(copy);
            }
        }

        // RGBA rows from Unity (bottom row first) as an 8-bit RGBA PNG.
        private static byte[] EncodePng(byte[] rgba, int width, int height)
        {
            var raw = new byte[(width * 4 + 1) * height];
            for (int row = 0; row < height; row++)
                Buffer.BlockCopy(rgba, (height - 1 - row) * width * 4, raw, row * (width * 4 + 1) + 1, width * 4);
            byte[] compressed;
            using (var buffer = new MemoryStream())
            {
                using (var zlib = new System.IO.Compression.ZLibStream(buffer, System.IO.Compression.CompressionLevel.Optimal))
                    zlib.Write(raw, 0, raw.Length);
                compressed = buffer.ToArray();
            }
            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8;  // bit depth
            header[9] = 6;  // RGBA
            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, 0, 8);
                WriteChunk(png, "IHDR", header);
                WriteChunk(png, "IDAT", compressed);
                WriteChunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var bytes = new byte[12 + data.Length];
            WriteBigEndian(bytes, 0, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, bytes, 4);
            Buffer.BlockCopy(data, 0, bytes, 8, data.Length);
            uint crc = 0xffffffff;
            for (int i = 4; i < 8 + data.Length; i++)
            {
                crc ^= bytes[i];
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ (0xedb88320 & (uint)-(int)(crc & 1));
            }
            WriteBigEndian(bytes, 8 + data.Length, ~crc);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteBigEndian(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        private static VisualElement BuildOverlay(VisualElement picture, out Label message)
        {
            if (picture == null)
                throw new InvalidOperationException("FM26 player picture was not found.");
            var previous = picture.Q<VisualElement>("fmface-generating-overlay");
            if (previous != null)
                previous.RemoveFromHierarchy();
            var overlay = new VisualElement { name = "fmface-generating-overlay", pickingMode = PickingMode.Ignore };
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0;
            overlay.style.top = 0;
            overlay.style.width = Length.Percent(100);
            overlay.style.height = Length.Percent(100);
            overlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.72f);
            overlay.style.justifyContent = Justify.Center;
            overlay.style.alignItems = Align.Center;
            message = new Label("Generating ◐") { pickingMode = PickingMode.Ignore };
            message.style.color = Color.white;
            message.style.fontSize = 14;
            message.style.unityTextAlign = TextAnchor.MiddleCenter;
            message.style.whiteSpace = WhiteSpace.Normal;
            message.style.width = Length.Percent(95);
            overlay.Add(message);
            picture.Add(overlay);
            int frame = 0;
            string[] frames = { "◐", "◓", "◑", "◒" };
            var label = message;
            overlay.schedule.Execute((Il2CppSystem.Action)(System.Action)(() =>
            {
                if (overlay.parent != null && !label.text.StartsWith("Face generation failed") &&
                    !label.text.StartsWith("Portrait saved") && label.text != "Loading face…")
                    label.text = "Generating " + frames[frame++ % frames.Length];
            })).Every(180);
            return overlay;
        }

        private static void ShowError(VisualElement overlay, Label message, string error)
        {
            if (overlay == null || message == null)
                return;
            message.text = "Face generation failed";
            overlay.tooltip = error ?? "Unknown error";
            overlay.style.backgroundColor = new Color(0.4f, 0f, 0f, 0.82f);
        }

        private sealed class GenerationResult
        {
            public bool Success;
            public string Error;
            public string GraphicsDir;
        }

        private static async Task<GenerationResult> RequestPortrait(long playerId, ProfileSnapshot profile,
            string mode = "generate", string referencePng = null)
        {
            string marker = null;
            try
            {
                string folder = GraphicsFolder();
                Directory.CreateDirectory(folder);
                string token = Guid.NewGuid().ToString("N");
                marker = Path.Combine(folder, ".fmface-location-" + token);
                File.WriteAllText(marker, token, Encoding.ASCII);
                var payload = new
                {
                    playerId = playerId.ToString(),
                    locationToken = token,
                    mode,
                    referencePng,
                    profile = new
                    {
                        type = profile.Type,
                        name = profile.Name, age = profile.Age, nationality = profile.Nationality,
                        jobTitle = profile.JobTitle, biographyHeadline = profile.BiographyHeadline,
                        position = profile.Position, club = profile.Club, height = profile.Height,
                        physical = profile.Physical, technical = profile.Technical,
                        mental = profile.Mental, goalkeeping = profile.Goalkeeping
                    }
                };
                using (var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"))
                using (var response = await Client.PostAsync("http://127.0.0.1:8767/generate-auto", content).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        using (var json = JsonDocument.Parse(body))
                        {
                            var result = json.RootElement;
                            var returnedToken = result.GetProperty("locationToken").GetString();
                            var savedFolder = result.GetProperty("graphicsDir").GetString();
                            if (!string.Equals(returnedToken, token, StringComparison.Ordinal) ||
                                string.IsNullOrWhiteSpace(savedFolder))
                                return new GenerationResult { Error = "Generator did not confirm the FM26 graphics folder." };
                            return new GenerationResult { Success = true, GraphicsDir = savedFolder };
                        }
                    }
                    try
                    {
                        using (var json = JsonDocument.Parse(body))
                            return new GenerationResult { Error = json.RootElement.GetProperty("error").GetString() };
                    }
                    catch (Exception) { }
                    return new GenerationResult { Error = "Local generator returned HTTP " + (int)response.StatusCode };
                }
            }
            catch (Exception error)
            {
                return new GenerationResult { Error = error.Message + " (start fmface.py serve if it is stopped)" };
            }
            finally
            {
                if (marker != null)
                    try { File.Delete(marker); }
                    catch (Exception error) { Instance?.Log.LogWarning("Could not remove graphics-folder check file: " + error.Message); }
            }
        }

        private static string GraphicsFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Sports Interactive", "Football Manager 26", "graphics", "fmface");
        }

        private bool Refresh()
        {
            if (WaitingForGraphics)
            {
                Log.LogWarning("Graphics refresh is already in progress");
                return false;
            }
            string folder = GraphicsFolder();
            if (!Directory.Exists(folder) || !File.Exists(Path.Combine(folder, "config.xml")))
            {
                Log.LogWarning("FM Face graphics folder or config.xml was not found: " + folder);
                return false;
            }
            var resources = Resources.FindObjectsOfTypeAll<ResourcesSubsystem>();
            if (resources.Length != 1)
            {
                Log.LogWarning("Expected one FM26 ResourcesSubsystem, found " + resources.Length);
                return false;
            }
            try
            {
                WaitingForGraphics = true;
                _refreshStartedUtc = System.DateTime.UtcNow;
                resources[0].RegisterLocalPath(folder, null);
                Log.LogInfo("Requested FM Face graphics reload from " + folder);
                return true;
            }
            catch (Exception error)
            {
                WaitingForGraphics = false;
                Log.LogError("Could not register FM Face graphics: " + error);
                return false;
            }
        }

        private void CheckRefreshTimeout()
        {
            if (!WaitingForGraphics || System.DateTime.UtcNow - _refreshStartedUtc < GraphicsTimeout)
                return;
            WaitingForGraphics = false;
            ShowError(_refreshOverlay, _refreshMessage,
                "Portrait saved, but graphics reload timed out. Restart FM26 to load it.");
            _refreshOverlay = null;
            _refreshMessage = null;
            Log.LogWarning("FM Face graphics reload timed out after 30 seconds; refresh lock released");
        }

        private void GraphicsLoaded()
        {
            try
            {
                var panels = Resources.FindObjectsOfTypeAll<PanelManager>();
                if (panels.Length != 1)
                    throw new InvalidOperationException("Expected one PanelManager; found " + panels.Length);
                panels[0].ReloadPanels();
                _graphicsLoadSerial++;
                if (_refreshOverlay != null && _refreshOverlay.parent != null)
                    _refreshOverlay.RemoveFromHierarchy();
                Log.LogInfo("FM Face graphics loaded and panels refreshed");
            }
            catch (Exception error)
            {
                ShowError(_refreshOverlay, _refreshMessage,
                    "Portrait saved, but panel refresh failed. Restart FM26 to load it.");
                Log.LogError("Graphics loaded, but panel refresh failed: " + error);
            }
            finally
            {
                _refreshOverlay = null;
                _refreshMessage = null;
            }
        }

        [HarmonyPatch(typeof(ResourcesSubsystem), "EnumerateLocalAssetsComplete")]
        private static class AssetLoadPatch
        {
            private static void Postfix()
            {
                if (!WaitingForGraphics)
                    return;
                WaitingForGraphics = false;
                Instance.GraphicsLoaded();
            }
        }

        [HarmonyPatch(typeof(ContextMenuSubsystem), "OpenContextMenu")]
        private static class MenuOpenPatch
        {
            private static void Prefix(MenuContext context)
            {
                if (Instance == null || context == null)
                    return;
                try
                {
                    Instance.ClosePortraitMenu();
                    var root = CurrentRoot();
                    var header = FindNamed(root, "ReportHeader");
                    if (header == null || !IsActionTarget(context.Target, header) ||
                        !HeaderId(TextValues(header), out long personId))
                        return;
                    var contributor = Instance._menuContributor.TryCast<IMenuContributor>();
                    context.AddContributor(contributor);
                }
                catch (Exception error)
                {
                    Instance.Log.LogWarning("Could not add FM Face Actions item: " + error);
                }
            }

            private static void Postfix(ContextMenuSubsystem __instance, MenuContext context)
            {
                if (Instance == null || context == null || !IsActionTarget(context.Target,
                    FindNamed(CurrentRoot(), "ReportHeader")))
                    return;
                var menu = __instance.m_cascadingMenuPopup?.Menu;
                if (menu != null)
                    menu.schedule.Execute((Il2CppSystem.Action)(System.Action)(() =>
                    {
                        foreach (var text in new[] { "Generate face", "Update face" })
                        {
                            var row = FindText(menu, text)?.parent?.parent;
                            if (row?.parent?.name == "View")
                                row.BringToFront();
                        }
                        Instance.Log.LogInfo("FM Face actions moved to bottom of Actions menu");
                    })).ExecuteLater(1500);
            }

        }

        public sealed class FaceMenuContributor : Il2CppSystem.Object
        {
            private bool _bound;

            public FaceMenuContributor(IntPtr pointer) : base(pointer) { }
            public FaceMenuContributor() : base(ClassInjector.DerivedConstructorPointer<FaceMenuContributor>())
            {
                ClassInjector.DerivedConstructorBody(this);
            }

            public bool IsBoundToMenu => _bound;

            public void BindToMenu(ref MenuContext context, MenuDescription.EntryID parent)
            {
                var root = CurrentRoot();
                var header = FindNamed(root, "ReportHeader");
                if (header == null || !IsActionTarget(context.Target, header) ||
                    !HeaderId(TextValues(header), out long personId))
                    return;
                bool staff = !IsPlayerOverview(root) && !IsPlayerHeader(header);
                context.Description.AddItem(parent, "Generate face",
                    (Il2CppSystem.Action)(System.Action)(() => Instance.Generate(personId, staff)));
                var update = context.Description.AddItem(parent, "Update face",
                    (Il2CppSystem.Action)(System.Action)(() => Instance.Generate(personId, staff, true)));
                // Updating needs a face to start from: a generated one or the game's photo.
                if (!HasFace(FindNamed(header, "PersonPicture")))
                    context.Description.SetEnabled(update, false);
                _bound = true;
                Instance.Log.LogInfo("FM Face action contributed to " + (staff ? "staff" : "player") +
                                     " Actions menu for " + personId);
            }

            public void UnbindFromMenu()
            {
                _bound = false;
            }

        }

        private static bool IsActionTarget(VisualElement target, VisualElement header)
        {
            bool namedAction = false;
            bool staffAction = false;
            for (var element = target; element != null; element = element.parent)
            {
                if ((element.name ?? "").IndexOf("FrameActions", StringComparison.OrdinalIgnoreCase) >= 0)
                    namedAction = true;
                if (element.worldBound.width < 250 && TextValues(element).Exists(value => value == "Actions"))
                    staffAction = true;
                if (element == header)
                    return namedAction;
                if (element.name == "report-player-tabs" && !IsPlayerHeader(header))
                    return staffAction;
            }
            return false;
        }
    }
}
