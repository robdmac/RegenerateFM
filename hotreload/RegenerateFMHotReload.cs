using System;
using System.Collections.Generic;
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

namespace RegenerateFMHotReload
{
    [BepInPlugin("regeneratefm.hotreload", "RegenerateFM", "0.9.0")]
    public sealed class HotReloadPlugin : BasePlugin
    {
        internal static HotReloadPlugin Instance;
        internal static bool WaitingForGraphics;
        private static readonly Regex Tags = new Regex("<[^>]*>", RegexOptions.Compiled);
        private static readonly Regex AgePattern = new Regex(@"\b(\d{1,2}) years old\b", RegexOptions.Compiled);
        private static readonly Regex IdPattern = new Regex(@"\bID:\s*(\d{1,20})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StatPattern = new Regex(@"^(?:[1-9]|1[0-9]|20)(?:[-–](?:[1-9]|1[0-9]|20))?$", RegexOptions.Compiled);
        // One position or several, comma-separated: "Attacking Midfielder (Right), Striker (Centre)".
        private const string OnePosition = @"(?:Goalkeeper|Defender|Wing[- ]Back|Defensive Midfielder|Midfielder|Attacking Midfielder|Striker)(?:\s*\([^)]{1,30}\))?";
        private static readonly Regex PositionPattern = new Regex("^" + OnePosition + @"(?:\s*,\s*" + OnePosition + ")*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
        // macOS: when FM26 last registered a file from a folder being reloaded (LocatorPatch).
        private static System.DateTime? _lastLocatorUtc;
        private static readonly TimeSpan LocatorsSettled = TimeSpan.FromMilliseconds(250);
        private readonly Dictionary<long, string> _biographyCache = new Dictionary<long, string>();
        private Harmony _harmony;
        private FaceMenuContributor _menuContributor;
        private VisualElement _keyDialog;
        private TextField _keyField;
        private bool _keyTyping;
        private Task<string> _keyCheck;
        private string _keyChecking;
        private Label _keyStatus;
        private Action _afterKeySaved;
        private BepInEx.Configuration.ConfigEntry<string> _model;
        private BepInEx.Configuration.ConfigEntry<string> _quality;
        private BepInEx.Configuration.ConfigEntry<string> _apiBaseUrl;
        private BepInEx.Configuration.ConfigEntry<string> _creditsUrl;
        // RegenerateFM credits sign-in in progress (Credits): starting, then polling.
        private Task<Credits.SignIn> _creditsStart;
        private Credits.SignIn _creditsSignIn;
        private Task<Credits.Poll> _creditsPoll;
        private System.DateTime _creditsNextPollUtc;
        // The key dialog is open for a game already signed in to RegenerateFM credits
        // (out of credits), so Buy credits opens the credits page.
        private bool _keyDialogSignedIn;
        private static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan GraphicsTimeout = TimeSpan.FromSeconds(30);
        // How long the squad batch waits for an opened profile to show all it reads
        // (within the batch's 45 s limit for each step).
        private static readonly TimeSpan ProfileBindTimeout = TimeSpan.FromSeconds(30);
        // How long an opened profile's attributes may show as unknown before the
        // batch takes the player as needing scouting rather than still loading.
        private static readonly TimeSpan ScoutingSettle = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan GenderWait = TimeSpan.FromSeconds(3);

        public override void Load()
        {
            if (!SupportedGameBuild())
            {
                Log.LogWarning("RegenerateFM supports only the checked FM26 26.3.2 and FM26 Demo binaries; buttons disabled");
                return;
            }
            Instance = this;
            BindSettings();
            ClassInjector.RegisterTypeInIl2Cpp<FaceMenuContributor>(new RegisterTypeOptions
            {
                Interfaces = new[] { typeof(IMenuContributor) }
            });
            _menuContributor = new FaceMenuContributor();
            _harmony = new Harmony("regeneratefm.hotreload");
            // See LocatorPatch: on macOS the reload is followed another way.
            if (OperatingSystem.IsMacOS())
                _harmony.PatchAll(typeof(LocatorPatch));
            else
                _harmony.PatchAll(typeof(AssetLoadPatch));
            _harmony.PatchAll(typeof(MenuOpenPatch));
            AddComponent<StaffWatcher>();
            Log.LogInfo("RegenerateFM Actions and portrait menu integration loaded");
        }

        // Model and quality are in BepInEx/config/regeneratefm.hotreload.cfg, reread
        // before each request. The key is kept apart (KeyStore) and asked for
        // in the game.
        private void BindSettings()
        {
            _model = Config.Bind("OpenAI", "Model", "gpt-image-2.5-sunburst", "OpenAI image model.");
            _quality = Config.Bind("OpenAI", "Quality", "medium",
                new BepInEx.Configuration.ConfigDescription("Rendering quality; higher costs more per face.",
                    new BepInEx.Configuration.AcceptableValueList<string>(FaceGenerator.Qualities)));
            _apiBaseUrl = Config.Bind("OpenAI", "ApiBaseUrl", "https://api.openai.com/v1",
                "Image API address. Leave this unchanged unless you are testing.");
            _creditsUrl = Config.Bind("Credits", "ServiceUrl", Credits.DefaultServiceUrl,
                "RegenerateFM credits service, used when you buy credits instead of using an OpenAI key.");
        }

        private ImageSettings CurrentSettings()
        {
            Config.Reload();
            string key;
            try
            {
                key = KeyStore.Load(KeyStore.DefaultPath);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                Log.LogWarning("Could not read the saved OpenAI key: " + error.Message);
                key = null;
            }
            var settings = new ImageSettings
            {
                ApiKey = key ?? "",
                Model = (_model.Value ?? "").Trim(),
                Quality = (_quality.Value ?? "").Trim().ToLowerInvariant(),
                BaseUrl = BaseUrlFor(key)
            };
            FaceGenerator.CheckSettings(settings);
            return settings;
        }

        // OpenAI's address (or ApiBaseUrl when testing), or the credits
        // service's for a RegenerateFM credits token.
        private string BaseUrlFor(string key)
        {
            return Credits.IsToken(key) ? Credits.ApiBase(CreditsServiceUrl()) : (_apiBaseUrl.Value ?? "").Trim();
        }

        private string CreditsServiceUrl()
        {
            string url = (_creditsUrl.Value ?? "").Trim().TrimEnd('/');
            return url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://127.0.0.1", StringComparison.Ordinal)
                ? url : Credits.DefaultServiceUrl;
        }

        private bool SupportedGameBuild()
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    string contents = Path.Combine(Paths.GameRootPath, "fm.app", "Contents");
                    string macAssembly = Path.Combine(contents, "Frameworks", "GameAssembly.dylib");
                    string macPlugin = Path.Combine(contents, "PlugIns", "game_plugin.bundle", "Contents", "MacOS", "game_plugin");
                    if (HasHash(macAssembly, "7886b66692c18504de32ad3973fe4c9afc3edfe12a6941b44e83627cb9563e26")
                        && HasHash(macPlugin, "de6b048ab1a95fd4ae683a7d751a33645183a9a570fa1c2fba32fd308ca87979"))
                        return true;
                    // The FM26 Demo for Mac, 26.3.2 too, with its own user data.
                    if (HasHash(macAssembly, "b3f86da82f7ef5b24d5a0cae4d4cccd4f904d0e8b971d3e7b92c78500a14eda5")
                        && HasHash(macPlugin, "ef6fb6078a5dfceaa325799caf00bb635f3a7dbd8cc4aa3021aa7beeccf8cc4d"))
                    {
                        UserDataName = "Football Manager 26 Demo";
                        return true;
                    }
                    return false;
                }
                string gameAssembly = Path.Combine(Paths.GameRootPath, "GameAssembly.dll");
                string gamePlugin = Path.Combine(Paths.GameRootPath, "fm_Data", "Plugins", "x86_64", "game_plugin.dll");
                if (HasHash(gameAssembly, "7ce3eb474dc6093df633f979e869e55b2ec7953fde2e732392a694d379ff7a0c")
                    && HasHash(gamePlugin, "eb6c86fab56e051fe41482c7b93d8cb0af716a1cc2c6197ce1babbff68372bb8"))
                    return true;
                // The FM26 Demo (Steam app 3551360) is a separate 26.3.2 build
                // that keeps its user data in "Football Manager 26 Demo".
                if (HasHash(gameAssembly, "edcd74617b881817da1895f4272add8fcfcab9d15a4ab64d858d408f59c730f0")
                    && HasHash(gamePlugin, "a4181cf61a265058d0b447fd0d679d5ac282b08a7962f0f0d67dbd27bdf9aa26"))
                {
                    UserDataName = "Football Manager 26 Demo";
                    return true;
                }
                return false;
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
            if (!KeyReady(() => Generate(personId, staff, update)))
                return;
            VisualElement picture = null;
            VisualElement root = null;
            VisualElement overlay = null;
            Label message = null;
            try
            {
                var snapshot = staff ? CaptureStaff(personId.Value, out picture, out root)
                                     : CaptureProfile(personId.Value, out picture, out root);
                _scoutingRetry = null;
                if (snapshot.BiographyHeadline == null && _biographyCache.TryGetValue(personId.Value, out string cachedBiography))
                    snapshot.BiographyHeadline = cachedBiography;
                var settings = CurrentSettings();
                byte[] reference = null;
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
                            (snapshot.BiographyHeadline != null) + "; family: " +
                            (FamilyText(snapshot) ?? "none"));
                _pending = new PendingGeneration
                {
                    Task = RequestPortrait(personId.Value, snapshot, settings, update ? "update" : "generate", reference),
                    Overlay = overlay,
                    Message = message,
                    StartedUtc = System.DateTime.UtcNow
                };
            }
            catch (NeedsScoutingException error)
            {
                _pending = null;
                // A profile that has just opened can show its attributes as unknown
                // while they load; look again for a few seconds, as the batch does.
                var now = System.DateTime.UtcNow;
                if (_scoutingRetry == null || _scoutingRetry.PersonId != personId.Value)
                    _scoutingRetry = new ScoutingRetry { PersonId = personId.Value, FirstUtc = now };
                if (now - _scoutingRetry.FirstUtc < ScoutingSettle)
                {
                    _scoutingRetry.NextUtc = now.AddSeconds(1);
                    _scoutingRetry.Retry = () => Generate(personId, staff, update);
                    return;
                }
                _scoutingRetry = null;
                Log.LogInfo(error.Message);
                ShowDialog(error.Message, "Close", () => CancelSquadScan());
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
                ShowError(pending.Overlay, pending.Message, "Portrait request timed out. Try again.");
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
                    ShowKeyProblem(result);
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
            if (!HeaderId(header, out long visibleId))
                throw new InvalidOperationException("Could not read this player's ID from the profile. Reopen it and try again.");
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
            profile.Gender = HeaderGender(header, playerId);
            profile.Relatives = HeaderRelatives(header, playerId);
            // "PlayingTeam" is the contract club; a loaned player's header also
            // shows the loan club, so the face wears the kit they play in now.
            var loanClub = FirstText(FindNamed(header, "TeamAwayFromOrTo"));
            if (!string.IsNullOrWhiteSpace(loanClub))
                profile.Club = loanClub;
            // A free agent's header has no PlayingTeam; its contract box says "Free Agent".
            if (string.IsNullOrWhiteSpace(profile.Club) &&
                FirstText(FindNamed(header, "Frame 1000003767")) is string contract && FaceGenerator.NoClub(contract))
                profile.Club = contract.Trim();
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
            int shown = 0, unknown = 0;
            foreach (var row in NamedElements(attributes, "attributes_row"))
            {
                if (row.worldBound.width < 40 || row.worldBound.height < 10)
                    continue;
                var values = TextValues(row);
                if (values.Count < 2)
                    continue;
                string stat = values[0];
                string score = values[values.Count - 1].Replace(" ", "");
                // "-" is not scouted yet; a range ("11-14") is known, if vaguely.
                bool hidden = score == "-" || score == "–";
                if (!stat.Contains("Rating") && (hidden || StatPattern.IsMatch(score)))
                {
                    shown++;
                    if (hidden)
                        unknown++;
                }
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
            // Half or more not scouted is too little to describe them; otherwise the
            // known attributes are used.
            if (shown > 0 && unknown * 2 >= shown && !string.IsNullOrWhiteSpace(profile.Name))
                throw new NeedsScoutingException(profile.Name);
            if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Position) ||
                string.IsNullOrWhiteSpace(profile.Club) || string.IsNullOrWhiteSpace(profile.Nationality) ||
                string.IsNullOrWhiteSpace(profile.Height) || profile.Age < 14 ||
                profile.Technical.Count + profile.Mental.Count + profile.Goalkeeping.Count < 6)
                throw new InvalidOperationException("Incomplete profile (no API request): " +
                    "name=" + profile.Name + ", age=" + profile.Age + ", nationality=" + profile.Nationality +
                    ", position=" + profile.Position + ", club=" + profile.Club + ", height=" + profile.Height +
                    ", physical=" + profile.Physical.Count + ", technical=" + profile.Technical.Count +
                    ", mental=" + profile.Mental.Count + ", goalkeeping=" + profile.Goalkeeping.Count + ".");
            profile.BiographyHeadline = CaptureBiographyHeadline(root);
            return profile;
        }

        // A player with half or more of their attributes hidden until they are
        // scouted: too few are known to describe them, which is said in a dialog
        // rather than as a failure.
        private sealed class ScoutingRetry
        {
            public long PersonId;
            public System.DateTime FirstUtc;
            public System.DateTime NextUtc;
            public Action Retry;
        }

        private ScoutingRetry _scoutingRetry;

        // Runs a pending Generate again while its profile is still the one shown.
        private void PollScoutingRetry()
        {
            var retry = _scoutingRetry;
            if (retry?.Retry == null || System.DateTime.UtcNow < retry.NextUtc)
                return;
            var again = retry.Retry;
            retry.Retry = null;
            var header = FindNamed(CurrentRoot(), "ReportHeader");
            if (header == null || !HeaderId(header, out long shown) || shown != retry.PersonId)
            {
                _scoutingRetry = null;
                return;
            }
            again();
        }

        private sealed class NeedsScoutingException : InvalidOperationException
        {
            public NeedsScoutingException(string name)
                : base("Your scouts don't know enough about " + name + " yet: half or more of their " +
                       "attributes are unknown. Scout them first, and once most are known, RegenerateFM can make " +
                       "their face. No image was requested.") { }
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
            if (!HeaderId(header, out long visibleId) || visibleId != personId)
                throw new InvalidOperationException("Staff profile changed; reopen it and try again.");
            var profile = new ProfileSnapshot { Type = "staff", Gender = HeaderGender(header, personId),
                                                Relatives = HeaderRelatives(header, personId) };
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
            // Without screen IDs the header has no "(ID:n)" text: the name is in
            // the same box as a player's, and the job title follows it.
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                foreach (var value in TextValues(FindNamed(banner, "Frame 1000003381")))
                    if (!IdPattern.IsMatch(value))
                    {
                        profile.Name = value;
                        break;
                    }
                int nameIndex = profile.Name == null ? -1 : bannerTexts.IndexOf(profile.Name);
                if (nameIndex >= 0 && nameIndex + 1 < bannerTexts.Count && string.IsNullOrWhiteSpace(profile.JobTitle))
                    profile.JobTitle = bannerTexts[nameIndex + 1];
            }
            profile.JobTitle = FirstText(FindNamed(banner, "Frame 1000003901")) ?? profile.JobTitle;
            // A staff header has no PlayingTeam: the club is the contract box's first text.
            profile.Club = FirstText(FindNamed(header, "PlayingTeam")) ?? FirstText(FindNamed(header, "Frame 1000003767"));
            // Its nation is shown twice, as a code by the age ("ENG") and in full ("England").
            foreach (var nation in NamedElements(header, "Frame 1000003766"))
            {
                var name = FirstText(nation);
                if (!string.IsNullOrWhiteSpace(name) && (profile.Nationality == null || name.Length > profile.Nationality.Length))
                    profile.Nationality = name;
            }
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
            {
                Instance?.Log.LogWarning("Staff header, for diagnosis: " + DescribeTexts(banner));
                throw new InvalidOperationException("Incomplete staff profile (no API request): name=" + profile.Name +
                    ", age=" + profile.Age + ", nationality=" + profile.Nationality + ", job=" + profile.JobTitle + ".");
            }
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

        // The person a profile header shows. Its binding remapper maps "objectID"
        // to Person.UniqueId, the ID FM26 shows with "Show screen IDs in Title Bar",
        // so that preference isn't needed. Skins that print "(ID:n)" still work.
        private static bool HeaderId(VisualElement header, out long id)
        {
            return BoundObjectId(header, out id) || TextId(TextValues(header), out id);
        }

        private static bool BoundObjectId(VisualElement header, out long id)
        {
            id = 0;
            var remapper = ObjectIdRemapper(header);
            var view = remapper?.TryCast<IBindableElement>()?.GetView();
            if (view == null || !GameSubsystems.TryGet<Bindings>(out var bindings) || bindings == null)
                return false;
            var key = view.Key;
            if (!bindings.TryGetNode(ref key, out var node) || node == null)
                return false;
            for (var child = node.FirstChild; child != null; child = child.NextSibling)
                if (child.Name == "objectID")
                {
                    var value = child.Value;
                    return value != null && !value.IsNull && long.TryParse(value.AsString(), out id) && id > 0;
                }
            return false;
        }

        // FM26 binds a person's gender as Person.IsPersonFemale. The header's
        // objectID aliases Person.UniqueId, so the Person node is that alias's
        // parent. Bound values arrive a frame or more after the bind, so the
        // answer is kept by person ID, and the header is bound when it appears.
        private static readonly Dictionary<long, bool> FemaleById = new Dictionary<long, bool>();
        private static readonly Dictionary<long, Bindings.ValueChangedCallback> GenderCallbacks =
            new Dictionary<long, Bindings.ValueChangedCallback>();
        private const string GenderProperty = "IsPersonFemale";

        private void CheckGender()
        {
            var root = CurrentRoot();
            var header = root == null ? null : FindNamed(root, "ReportHeader");
            if (header != null && BoundObjectId(header, out long id))
            {
                WatchGender(header, id);
                WatchFavoured(header, id);
            }
        }

        private static string HeaderGender(VisualElement header, long id)
        {
            WatchGender(header, id);
            return FemaleById.TryGetValue(id, out bool female) ? (female ? "female" : "male") : null;
        }

        private static void WatchGender(VisualElement header, long id)
        {
            if (FemaleById.ContainsKey(id) || !PersonNodeKey(header, out var bindings, out var personKey))
                return;
            if (BindingPaths.Exists(bindings, personKey, GenderProperty))
            {
                var value = BindingPaths.Get(bindings, personKey, GenderProperty);
                if (value != null && !value.IsNull && bool.TryParse(value.AsString(), out bool known))
                {
                    FemaleById[id] = known;
                    Instance?.Log.LogInfo($"Person {id} is {(known ? "female" : "male")}");
                    return;
                }
            }
            if (GenderCallbacks.ContainsKey(id))
                return;
            Bindings.ValueChangedCallback callback = null;
            callback = new Action<Bindings.Key, SI.Core.TypedValue>((key, value) =>
            {
                if (value == null || value.IsNull || !bool.TryParse(value.AsString(), out bool female))
                    return;
                FemaleById[id] = female;
                Instance?.Log.LogInfo($"Person {id} is {(female ? "female" : "male")}");
                // One answer is enough; drop the binding so viewed people don't pile up.
                if (GenderCallbacks.Remove(id) && GameSubsystems.TryGet<Bindings>(out var current) && current != null)
                {
                    var unbindKey = key;
                    try { current.Unbind(ref unbindKey, callback); }
                    catch (Exception) { }
                }
            });
            GenderCallbacks[id] = callback;
            BindingPaths.Bind(bindings, personKey, GenderProperty, callback);
        }

        private static bool PersonNodeKey(VisualElement header, out Bindings bindings, out Bindings.Key personKey)
        {
            personKey = default;
            bindings = null;
            var view = ObjectIdRemapper(header)?.TryCast<IBindableElement>()?.GetView();
            if (view == null || !GameSubsystems.TryGet<Bindings>(out bindings) || bindings == null)
                return false;
            var key = view.Key;
            if (!bindings.TryGetNode(ref key, out var node) || node == null)
                return false;
            for (var child = node.FirstChild; child != null; child = child.NextSibling)
                if (child.Name == "objectID")
                {
                    var alias = child.Alias;
                    if (!bindings.TryGetNode(ref alias, out var target) || target?.Parent == null)
                        return false;
                    personKey = target.Parent.Key;
                    return true;
                }
            return false;
        }

        // FM26 lists a person's family under Person.FavouredPeople, with
        // team-mates and former coaches: a list of groups, each a DisplayString
        // ("Brothers", "Father") and a PersonList. Each group, and then each
        // person, is copied into a node of RegenerateFM's own so that its
        // properties can be bound, as FM26's own lists do. Only the person on
        // screen keeps those nodes and bindings; a person whose list was still
        // arriving when another opened is read again next time.
        private static readonly Dictionary<long, List<(string Relation, string PersonId)>> FavouredById =
            new Dictionary<long, List<(string Relation, string PersonId)>>();
        private static readonly List<(Bindings.Key Root, string Path, Bindings.ValueChangedCallback Callback)> FavouredBindings =
            new List<(Bindings.Key, string, Bindings.ValueChangedCallback)>();
        private static readonly List<Bindings.Key> FavouredProbes = new List<Bindings.Key>();
        private static long _favouredId;
        private static int _favouredPending;
        private static int _probeSerial;

        private static string FamilyText(ProfileSnapshot profile)
        {
            try
            {
                var family = FaceGenerator.FamilyRelatives(profile);
                return family.Count == 0 ? null : string.Join(", ", family.Select(item => item.Relation + " " + item.PersonId));
            }
            catch (ArgumentException error) { return error.Message; }
        }

        private static List<(string Relation, string PersonId)> HeaderRelatives(VisualElement header, long id)
        {
            WatchFavoured(header, id);
            return FavouredById.TryGetValue(id, out var found) && !(id == _favouredId && _favouredPending > 0)
                ? new List<(string Relation, string PersonId)>(found) : new List<(string Relation, string PersonId)>();
        }

        private static void WatchFavoured(VisualElement header, long id)
        {
            if (id == _favouredId || !PersonNodeKey(header, out var bindings, out var personKey))
                return;
            ReleaseFavoured(bindings);
            if (FavouredById.ContainsKey(id))
                return;
            _favouredId = id;
            var found = FavouredById[id] = new List<(string Relation, string PersonId)>();
            BindValue(bindings, personKey, "FavouredPeople.FavouredPeopleReasonList", groups =>
            {
                var list = groups.Get()?.TryCast<Il2CppSystem.Collections.Generic.List<SI.Core.TypedValue>>();
                for (int i = 0; list != null && i < list.Count; i++)
                {
                    var group = Probe(bindings, list[i]);
                    BindValue(bindings, group, "DisplayString", display =>
                    {
                        string relation = display.AsString();
                        BindValue(bindings, group, "PersonList", people =>
                        {
                            var members = people.Get()?.TryCast<Il2CppSystem.Collections.Generic.List<SI.Core.TypedValue>>();
                            for (int j = 0; members != null && j < members.Count; j++)
                                BindValue(bindings, Probe(bindings, members[j]), "UniqueId", unique =>
                                {
                                    if (long.TryParse(unique.AsString(), out long other) && other > 0 &&
                                        !found.Contains((relation, other.ToString())))
                                        found.Add((relation, other.ToString()));
                                });
                        });
                    });
                }
            });
        }

        // Drops the nodes and bindings made for the last person; their list is
        // forgotten if it hadn't all arrived.
        private static void ReleaseFavoured(Bindings bindings)
        {
            if (_favouredPending > 0)
                FavouredById.Remove(_favouredId);
            foreach (var (root, path, callback) in FavouredBindings)
            {
                try { BindingPaths.Unbind(bindings, root, path, callback); }
                catch (Exception) { }
            }
            foreach (var probe in FavouredProbes)
            {
                var key = probe;
                try { bindings.Remove(ref key); }
                catch (Exception) { }
            }
            FavouredBindings.Clear();
            FavouredProbes.Clear();
            _favouredId = 0;
            _favouredPending = 0;
        }

        // A node of RegenerateFM's own holding ``value``, so its properties can be bound.
        private static Bindings.Key Probe(Bindings bindings, SI.Core.TypedValue value)
        {
            var key = BindingPaths.Set(bindings, "RegenerateFMFavoured" + (++_probeSerial), value);
            FavouredProbes.Add(key);
            return key;
        }

        // Binds root's path; got runs once, with the first value FM26 sends.
        private static void BindValue(Bindings bindings, Bindings.Key root, string path, Action<SI.Core.TypedValue> got)
        {
            bool answered = false;
            Bindings.ValueChangedCallback callback = new Action<Bindings.Key, SI.Core.TypedValue>((key, value) =>
            {
                if (answered || value == null || value.IsNull)
                    return;
                answered = true;
                _favouredPending--;
                try { got(value); }
                catch (Exception error) { Instance?.Log.LogWarning("Reading " + path + " failed: " + error.Message); }
            });
            _favouredPending++;
            FavouredBindings.Add((root, path, callback));
            BindingPaths.Bind(bindings, root, path, callback);
        }

        // Bindings' methods that take a path. Il2CppInterop's wrappers pass a
        // ref ReadOnlySpan<char> as the boxed span rather than its contents. Under
        // Proton with BepInEx be.760, FM26 then hashed an empty path (HashPath gave
        // the empty string's hash) and answered for the Person node itself, so
        // IsPersonFemale and FavouredPeople never arrived. These pass the span's
        // contents, which is what FM26's code expects.
        private static class BindingPaths
        {
            private static IntPtr Method(string name) => (IntPtr)typeof(Bindings)
                .GetField("NativeMethodInfoPtr_" + name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .GetValue(null);

            private static readonly IntPtr ExistsMethod = Method("Exists_Public_Boolean_byref_Key_byref_ReadOnlySpan_1_Char_0");
            private static readonly IntPtr GetMethod = Method("Get_Public_TypedValue_byref_Key_byref_ReadOnlySpan_1_Char_0");
            private static readonly IntPtr BindMethod = Method("Bind_Public_Key_byref_Key_byref_ReadOnlySpan_1_Char_ValueChangedCallback_0");
            private static readonly IntPtr UnbindMethod = Method("Unbind_Public_Void_byref_Key_byref_ReadOnlySpan_1_Char_ValueChangedCallback_0");
            private static readonly IntPtr SetMethod = Method("Set_Public_Key_byref_ReadOnlySpan_1_Char_TypedValue_NodeFlags_SetFlags_0");

            // Calls method with the span's contents (and the root key, if any)
            // before the other arguments.
            private static unsafe IntPtr Invoke(IntPtr method, Bindings bindings, Bindings.Key* root, string path,
                                                params IntPtr[] rest)
            {
                Il2CppSystem.String text = path;
                var span = Il2CppSystem.MemoryExtensions.AsSpan(text);
                int count = (root != null ? 1 : 0) + 1 + rest.Length;
                IntPtr* args = stackalloc IntPtr[count];
                int next = 0;
                if (root != null)
                    args[next++] = (IntPtr)root;
                args[next++] = IL2CPP.il2cpp_object_unbox(IL2CPP.Il2CppObjectBaseToPtrNotNull(span));
                foreach (var arg in rest)
                    args[next++] = arg;
                IntPtr error = IntPtr.Zero;
                IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, IL2CPP.Il2CppObjectBaseToPtrNotNull(bindings), (void**)args, ref error);
                GC.KeepAlive(text);
                GC.KeepAlive(span);
                Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(error);
                return result;
            }

            public static unsafe bool Exists(Bindings bindings, Bindings.Key root, string path) =>
                *(bool*)IL2CPP.il2cpp_object_unbox(Invoke(ExistsMethod, bindings, &root, path));

            public static unsafe SI.Core.TypedValue Get(Bindings bindings, Bindings.Key root, string path)
            {
                IntPtr value = Invoke(GetMethod, bindings, &root, path);
                return value == IntPtr.Zero ? null : Il2CppInterop.Runtime.Runtime.Il2CppObjectPool.Get<SI.Core.TypedValue>(value);
            }

            public static unsafe Bindings.Key Bind(Bindings bindings, Bindings.Key root, string path, Bindings.ValueChangedCallback callback)
            {
                var key = *(Bindings.Key*)IL2CPP.il2cpp_object_unbox(
                    Invoke(BindMethod, bindings, &root, path, IL2CPP.Il2CppObjectBaseToPtr(callback)));
                GC.KeepAlive(callback);
                return key;
            }

            public static unsafe void Unbind(Bindings bindings, Bindings.Key root, string path, Bindings.ValueChangedCallback callback)
            {
                Invoke(UnbindMethod, bindings, &root, path, IL2CPP.Il2CppObjectBaseToPtr(callback));
                GC.KeepAlive(callback);
            }

            // A node at path from the root, holding value.
            public static unsafe Bindings.Key Set(Bindings bindings, string path, SI.Core.TypedValue value)
            {
                byte flags = 0;
                int setFlags = 0;
                var key = *(Bindings.Key*)IL2CPP.il2cpp_object_unbox(
                    Invoke(SetMethod, bindings, null, path, IL2CPP.Il2CppObjectBaseToPtr(value), (IntPtr)(&flags), (IntPtr)(&setFlags)));
                GC.KeepAlive(value);
                return key;
            }
        }

        private static BindingRemapper ObjectIdRemapper(VisualElement element)
        {
            if (element == null)
                return null;
            var remapper = element.TryCast<BindingRemapper>();
            if (remapper?.Mappings != null)
                foreach (var mapping in remapper.Mappings)
                    if (mapping.from == "objectID" && (mapping.to ?? "").EndsWith(".UniqueId", StringComparison.OrdinalIgnoreCase))
                        return remapper;
            for (int i = 0; i < element.hierarchy.childCount; i++)
            {
                var found = ObjectIdRemapper(element.hierarchy.ElementAt(i));
                if (found != null)
                    return found;
            }
            return null;
        }

        private static bool TextId(List<string> values, out long id)
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
                plugin.PollKeyCheck();
                plugin.PollCreditsSignIn();
                plugin.PollKeyTyping();
                try { plugin.PollScoutingRetry(); }
                catch (Exception error) { plugin.Log.LogWarning("Profile check failed: " + error.Message); }
                if (Time.unscaledTime < _nextScan)
                    return;
                _nextScan = Time.unscaledTime + 0.5f;
                try { plugin.CheckGender(); }
                catch (Exception error) { plugin.Log.LogWarning("Gender scan failed: " + error.Message); }
                try { plugin.CheckBiographyCache(); }
                catch (Exception error) { plugin.Log.LogWarning("Biography scan failed: " + error.Message); }
                try { plugin.CheckPortraitTrigger(); }
                catch (Exception error) { plugin.Log.LogWarning("Portrait menu setup failed: " + error.Message); }
                try { plugin.CheckDialogPage(); }
                catch (Exception error) { plugin.Log.LogWarning("Dialog check failed: " + error.Message); }
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
            var menuItem = FindText(root, "Squad Numbers");
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

        // True when a key is saved. Otherwise opens the key dialog, which runs
        // `next` once a key has been checked and saved.
        private bool KeyReady(Action next)
        {
            try
            {
                if (KeyStore.Load(KeyStore.DefaultPath) != null)
                    return true;
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not read the saved OpenAI key: " + error.Message);
                ShowKeyDialog("RegenerateFM couldn't read the saved key. Paste it again.", next);
                return false;
            }
            ShowKeyDialog(null, next);
            return false;
        }

        private void ShowKeyDialog(string problem, Action next, bool signedIn = false)
        {
            CloseKeyDialog();
            _keyDialogSignedIn = signedIn;
            var root = CurrentRoot();
            if (root == null)
                return;
            var dialog = new VisualElement { name = "regeneratefm-key-dialog" };
            dialog.style.position = Position.Absolute;
            dialog.style.right = 24;
            dialog.style.top = 80;
            dialog.style.width = 420;
            dialog.style.paddingTop = 16;
            dialog.style.paddingBottom = 16;
            dialog.style.paddingLeft = 16;
            dialog.style.paddingRight = 16;
            dialog.style.backgroundColor = new Color(0.13f, 0.10f, 0.18f, 0.97f);
            Func<string, int, Label> text = (value, size) =>
            {
                var label = new Label(value) { pickingMode = PickingMode.Ignore };
                label.style.color = Color.white;
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.fontSize = size;
                label.style.marginBottom = 8;
                dialog.Add(label);
                return label;
            };
            text("RegenerateFM: paying for faces", 18);
            if (problem != null)
                text(problem, 14).style.color = new Color(1f, 0.82f, 0.45f, 1f);
            text("Buy RegenerateFM credits: one credit makes or updates one face. Packs go from 75 credits for " +
                 "£2 / €2.50 / $2.50 to 1000 for £20 / €24 / $24. You sign in with your email in your browser; " +
                 "there's nothing to paste.", 14);
            var buy = new Button((Il2CppSystem.Action)(System.Action)(() => BuyCredits()))
            { text = "Buy credits / Sign in", focusable = false };
            buy.style.height = 34;
            buy.style.marginBottom = 14;
            buy.style.color = Color.white;
            buy.style.backgroundColor = new Color(0.38f, 0.10f, 0.60f, 1f);
            dialog.Add(buy);
            text("Or use your own OpenAI API key. Each face is then one billed request on your OpenAI account, " +
                 "usually a few cents or less.", 13);
            var open = new Button((Il2CppSystem.Action)(System.Action)(() => Application.OpenURL("https://platform.openai.com/api-keys")))
            { text = "Get a key at platform.openai.com/api-keys", focusable = false };
            open.style.height = 30;
            open.style.marginBottom = 10;
            dialog.Add(open);
            var field = new TextField { isPasswordField = true, maskChar = '•' };
            // FM26's text field doesn't paste on macOS (the game has no Edit menu
            // for Cmd+V), so there is a Paste button, and Cmd+V or Ctrl+V reaching
            // the field is handled here too: the clipboard replaces its text.
            Action paste = () =>
            {
                var clip = (GUIUtility.systemCopyBuffer ?? "").Trim();
                if (clip.Length > 0)
                    field.value = clip;
                if (_keyStatus != null)
                    _keyStatus.text = clip.Length > 0
                        ? "Pasted " + clip.Length + " characters. Choose Save key."
                        : "The clipboard is empty. Copy your key, then choose Paste.";
            };
            field.RegisterCallback(DelegateSupport.ConvertDelegate<EventCallback<KeyDownEvent>>(
                new Action<KeyDownEvent>(key =>
                {
                    if (key.keyCode != KeyCode.V || !(key.commandKey || key.ctrlKey))
                        return;
                    paste();
                    key.StopPropagation();
                })), TrickleDown.TrickleDown);
            // The dialog is 420 wide with 16 padding each side: 388 for both.
            field.style.height = 30;
            field.style.width = 312;
            field.style.flexShrink = 0;
            var pasteButton = new Button((Il2CppSystem.Action)(System.Action)(() => paste()))
            { text = "Paste", focusable = false };
            pasteButton.style.height = 30;
            pasteButton.style.width = 70;
            pasteButton.style.marginLeft = 6;
            pasteButton.style.flexShrink = 0;
            var entry = new VisualElement();
            entry.style.flexDirection = FlexDirection.Row;
            entry.Add(field);
            entry.Add(pasteButton);
            dialog.Add(entry);
            _keyStatus = text("Paste or type the key (it starts with sk-), then choose Save key.", 13);
            _keyStatus.style.marginTop = 6;
            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.justifyContent = Justify.FlexEnd;
            var cancel = new Button((Il2CppSystem.Action)(System.Action)(() => CloseKeyDialog())) { text = "Cancel", focusable = false };
            var save = new Button((Il2CppSystem.Action)(System.Action)(() => SaveKey(field.value)))
            { text = "Save key", focusable = false };
            save.style.color = Color.white;
            save.style.backgroundColor = new Color(0.38f, 0.10f, 0.60f, 1f);
            foreach (var button in new[] { cancel, save })
            {
                button.style.height = 32;
                button.style.marginLeft = 8;
                buttons.Add(button);
            }
            dialog.Add(buttons);
            text("The key is kept on this computer only, " +
                 (OperatingSystem.IsWindows() ? "encrypted for your Windows user" : "readable only by your user account") + ", in " +
                 Path.GetDirectoryName(KeyStore.DefaultPath) + ". Delete that folder to forget it.", 12);
            var questions = new Button((Il2CppSystem.Action)(System.Action)(() => ShowKeyFaq(dialog)))
            { text = "Questions: why it costs money, and which option is cheaper", focusable = false };
            questions.style.height = 26;
            questions.style.marginTop = 4;
            questions.style.fontSize = 12;
            questions.style.color = new Color(0.78f, 0.70f, 1f, 1f);
            questions.style.backgroundColor = new Color(0, 0, 0, 0);
            questions.style.borderTopWidth = 0;
            questions.style.borderBottomWidth = 0;
            questions.style.borderLeftWidth = 0;
            questions.style.borderRightWidth = 0;
            dialog.Add(questions);
            root.Add(dialog);
            _keyDialog = dialog;
            _keyField = field;
            _afterKeySaved = next;
            field.Focus();
        }

        private void SaveKey(string value)
        {
            if (_keyCheck != null)
                return;
            string key;
            try
            {
                key = KeyStore.Clean(value);
            }
            catch (ArgumentException error)
            {
                _keyStatus.text = error.Message;
                return;
            }
            ImageSettings settings;
            try
            {
                settings = new ImageSettings { BaseUrl = BaseUrlFor(key) };
            }
            catch (Exception error)
            {
                _keyStatus.text = error.Message;
                return;
            }
            _keyStatus.text = Credits.IsToken(key) ? "Checking the RegenerateFM sign-in…" : "Checking the key with OpenAI (this is free)…";
            _keyChecking = key;
            _keyCheck = FaceGenerator.CheckKey(settings, key);
        }

        private void PollKeyCheck()
        {
            var check = _keyCheck;
            if (check == null || !check.IsCompleted)
                return;
            _keyCheck = null;
            string key = _keyChecking;
            _keyChecking = null;
            string problem;
            try
            {
                problem = check.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                problem = "Could not check the key: " + error.Message;
            }
            if (_keyDialog == null || _keyDialog.parent == null)
                return;
            if (problem != null)
            {
                _keyStatus.text = problem;
                return;
            }
            try
            {
                KeyStore.Save(KeyStore.DefaultPath, key);
            }
            catch (Exception error)
            {
                _keyStatus.text = "Could not save the key: " + error.Message;
                Log.LogError("Could not save the OpenAI key: " + error);
                return;
            }
            Log.LogInfo("OpenAI key checked and saved to " + KeyStore.DefaultPath);
            var next = _afterKeySaved;
            CloseKeyDialog();
            try
            {
                next?.Invoke();
            }
            catch (Exception error)
            {
                Log.LogError("Could not continue after saving the key: " + error);
            }
        }

        // The key dialog's questions (the same as the credits pages' FAQ, in
        // regeneratefm-server/src/pages.ts), shown in place of the dialog's
        // controls until Back. A question's link opens in the browser.
        private static readonly (string Question, string Answer, string LinkText, string Link)[] KeyFaq =
        {
            ("Why does making a face cost money?",
             "Each face is drawn by OpenAI's image model, and OpenAI charges for every image it makes. RegenerateFM doesn't run " +
             "its own AI, so every face is one paid request to OpenAI, whether you use credits or your own key.", null, null),
            ("Which is cheaper, credits or my own OpenAI API key?",
             "Your own key. You then pay OpenAI's price for each image directly, which at RegenerateFM's default quality is " +
             "about 1.2p (1.6 US cents) a face. Credits cost 2p to 2.7p each (2.4 to 3.3 cents in euros or dollars), " +
             "but you don't need an OpenAI account, billing set up with OpenAI, or a key to copy into the game.",
             "OpenAI's API pricing", "https://openai.com/api/pricing/"),
            ("Why do credits cost more than OpenAI's own price?",
             "Credits pay for more than the image: VAT or sales tax, the card payment (each payment has a fixed fee, which " +
             "is why small packs cost more per credit), Stripe handling tax, fraud and refunds for every sale, the service " +
             "that signs you in and passes each request on to OpenAI, and the credit we give back when an image fails.",
             null, null),
            ("Can I use my ChatGPT Plus or Pro subscription instead?",
             "Not yet. A ChatGPT subscription and OpenAI's API are separate products. OpenAI has started letting some apps " +
             "use a ChatGPT plan through \u201cSign in with ChatGPT\u201d, but it's a preview, and image generation is one " +
             "of the things it doesn't support yet. If OpenAI adds it, RegenerateFM will offer it.",
             "OpenAI: Sign in with ChatGPT limitations",
             "https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations"),
        };

        private void ShowKeyFaq(VisualElement dialog)
        {
            // Hide the dialog's controls (the typed key stays in its field) and show the answers.
            var hidden = new List<VisualElement>();
            for (int i = 0; i < dialog.childCount; i++)
            {
                var child = dialog[i];
                if (child.resolvedStyle.display != DisplayStyle.None)
                {
                    hidden.Add(child);
                    child.style.display = DisplayStyle.None;
                }
            }
            var faq = new ScrollView { name = "regeneratefm-key-faq" };
            faq.style.maxHeight = 460;
            Func<string, int, bool, Label> add = (value, size, bold) =>
            {
                var label = new Label(value) { pickingMode = PickingMode.Ignore };
                label.style.color = bold ? Color.white : new Color(0.82f, 0.80f, 0.88f, 1f);
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.fontSize = size;
                label.style.marginBottom = bold ? 4 : 6;
                if (bold)
                    label.style.unityFontStyleAndWeight = FontStyle.Bold;
                faq.Add(label);
                return label;
            };
            add("Questions", 18, true);
            foreach (var item in KeyFaq)
            {
                add(item.Question, 14, true).style.marginTop = 8;
                add(item.Answer, 13, false);
                if (item.Link != null)
                {
                    string url = item.Link;
                    var link = new Button((Il2CppSystem.Action)(System.Action)(() => Application.OpenURL(url)))
                    { text = item.LinkText + " \u2197", focusable = false };
                    link.style.alignSelf = Align.FlexStart;
                    link.style.height = 24;
                    link.style.fontSize = 12;
                    faq.Add(link);
                }
            }
            dialog.Add(faq);
            var back = new Button((Il2CppSystem.Action)(System.Action)(() =>
            {
                faq.RemoveFromHierarchy();
                foreach (var child in hidden)
                    child.style.display = StyleKeyword.Null;
            }))
            { text = "Back", focusable = false };
            back.style.height = 32;
            back.style.marginTop = 10;
            faq.Add(back);
        }

        // A failed face whose key needs attention: sign in again, or buy more credits.
        private void ShowKeyProblem(GenerationResult result)
        {
            string key = null;
            try { key = KeyStore.Load(KeyStore.DefaultPath); } catch (Exception) { }
            if (result.OutOfCredits)
                ShowKeyDialog("You've used all your RegenerateFM credits. Buy more to carry on.", null, signedIn: Credits.IsToken(key));
            else if (result.KeyRejected)
                ShowKeyDialog(Credits.IsToken(key)
                    ? "Your RegenerateFM sign-in isn't valid any more. Sign in again."
                    : "OpenAI didn't accept the saved key. Paste a new one.", null);
        }

        // Buy credits / Sign in: a signed-in game opens the credits page in the
        // browser; otherwise a device sign-in starts, and PollCreditsSignIn
        // saves the token once the player has finished in the browser.
        private void BuyCredits()
        {
            if (_creditsStart != null || _creditsSignIn != null)
                return;
            if (_keyDialogSignedIn)
            {
                Application.OpenURL(CreditsServiceUrl() + "/link");
                _keyStatus.text = "The credits page is open in your browser. Your new credits work as soon as you've paid.";
                return;
            }
            _keyStatus.text = "Opening your browser…";
            _creditsStart = Credits.Start(CreditsServiceUrl());
        }

        private void PollCreditsSignIn()
        {
            if (_creditsStart != null && _creditsStart.IsCompleted)
            {
                var start = _creditsStart;
                _creditsStart = null;
                try
                {
                    _creditsSignIn = start.GetAwaiter().GetResult();
                }
                catch (Exception error)
                {
                    Log.LogWarning("RegenerateFM credits sign-in didn't start: " + error.Message);
                    if (_keyStatus != null)
                        _keyStatus.text = "RegenerateFM credits can't be reached right now. Try again in a moment.";
                    return;
                }
                Application.OpenURL(_creditsSignIn.Link);
                _creditsNextPollUtc = System.DateTime.UtcNow.AddSeconds(_creditsSignIn.Interval);
                if (_keyStatus != null)
                    _keyStatus.text = "In your browser, sign in and confirm the code " + _creditsSignIn.UserCode +
                                      ". RegenerateFM is waiting…";
            }
            var signIn = _creditsSignIn;
            if (signIn == null)
                return;
            if (_keyDialog == null || _keyDialog.parent == null || System.DateTime.UtcNow > signIn.ExpiresUtc)
            {
                // Closed, or the code expired: stop waiting.
                if (_keyStatus != null && System.DateTime.UtcNow > signIn.ExpiresUtc)
                    _keyStatus.text = "The sign-in expired. Choose Buy credits / Sign in again.";
                _creditsSignIn = null;
                _creditsPoll = null;
                return;
            }
            if (_creditsPoll == null)
            {
                if (System.DateTime.UtcNow >= _creditsNextPollUtc)
                    _creditsPoll = Credits.Check(CreditsServiceUrl(), signIn.DeviceCode);
                return;
            }
            if (!_creditsPoll.IsCompleted)
                return;
            Credits.Poll poll;
            try
            {
                poll = _creditsPoll.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                Log.LogWarning("RegenerateFM credits sign-in check failed: " + error.Message);
                poll = new Credits.Poll { Status = "pending" };
            }
            _creditsPoll = null;
            _creditsNextPollUtc = System.DateTime.UtcNow.AddSeconds(signIn.Interval * (poll.Status == "slow_down" ? 2 : 1));
            if (poll.Status == "expired")
            {
                _creditsSignIn = null;
                _keyStatus.text = "The sign-in expired. Choose Buy credits / Sign in again.";
                return;
            }
            if (poll.Status != "approved")
                return;
            _creditsSignIn = null;
            try
            {
                KeyStore.Save(KeyStore.DefaultPath, poll.Token);
            }
            catch (Exception error)
            {
                _keyStatus.text = "Could not save the sign-in: " + error.Message;
                Log.LogError("Could not save the RegenerateFM credits token: " + error);
                return;
            }
            Log.LogInfo("Signed in to RegenerateFM credits (" + poll.Credits + " credits); token saved to " + KeyStore.DefaultPath);
            if (poll.Credits == 0)
            {
                // Signed in but nothing bought yet: keep the dialog so the player can buy.
                _keyStatus.text = "Signed in as " + poll.Email + ". You have no credits yet: choose a pack in your browser, " +
                                  "then choose the face again.";
                _keyDialogSignedIn = true;
                return;
            }
            var next = _afterKeySaved;
            CloseKeyDialog();
            try
            {
                next?.Invoke();
            }
            catch (Exception error)
            {
                Log.LogError("Could not continue after signing in: " + error);
            }
        }

        private void CloseKeyDialog()
        {
            if (_keyDialog != null && _keyDialog.parent != null)
                _keyDialog.RemoveFromHierarchy();
            _keyDialog = null;
            _keyField = null;
            _keyStatus = null;
            _afterKeySaved = null;
            PollKeyTyping();
        }

        // FM26 reads its keyboard shortcuts directly, so letters typed into the
        // key field would also drive the game. Its own text boxes switch on
        // "exclusive input" while in use; do the same while the dialog is open.
        private void PollKeyTyping()
        {
            bool open = _keyField != null && _keyField.panel != null;
            if (open == _keyTyping)
                return;
            try
            {
                var input = ManualSingleton<InputManager>.Instance;
                if (input == null)
                    return;
                if (open)
                    input.OnExclusiveInputBegin();
                else
                    input.OnExclusiveInputEnd();
                _keyTyping = open;
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not switch FM26's keyboard shortcuts for the key field: " + error.Message);
                _keyTyping = open;
            }
        }

        private Label ShowDialog(string text, string buttonText, Action buttonAction)
        {
            CancelSquadScan();
            var root = CurrentRoot();
            var dialog = new VisualElement { name = "regeneratefm-squad-dialog" };
            dialog.style.position = Position.Absolute;
            // Centred over the page.
            dialog.style.left = Math.Max(0f, (root.layout.width - 360f) / 2f);
            dialog.style.top = Math.Max(0f, root.layout.height / 3f - 80f);
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
            _dialogPage = DialogPage(root);
            _dialogPageMisses = 0;
            WatchDialogEscape(root);
            return label;
        }

        // The page a dialog was shown over: the profile's person, or the squad
        // list's team. A message closes when you leave that page or press Escape;
        // a running squad scan or batch keeps its dialog until it ends.
        private string _dialogPage;
        private int _dialogPageMisses;
        private VisualElement _escapeRoot;

        private static string DialogPage(VisualElement root)
        {
            var header = FindNamed(root, "ReportHeader");
            if (header != null && HeaderId(header, out long id))
                return "person " + id;
            return SquadTable(root) != null ? "squad " + (SquadPageTeam(root) ?? "") : null;
        }

        private void CheckDialogPage()
        {
            if (_squadDialog == null || _squadScan != null || _squadBatch != null)
                return;
            if (_squadDialog.parent == null)
            {
                _squadDialog = null;
                return;
            }
            // Twice in a row (a second apart), so a page redrawing doesn't close it.
            if (_dialogPage == null || DialogPage(CurrentRoot()) == _dialogPage)
                _dialogPageMisses = 0;
            else if (++_dialogPageMisses >= 2)
                CancelSquadScan();
        }

        private void WatchDialogEscape(VisualElement root)
        {
            var top = root?.panel?.visualTree;
            if (top == null || top == _escapeRoot)
                return;
            _escapeRoot = top;
            top.RegisterCallback(DelegateSupport.ConvertDelegate<EventCallback<KeyDownEvent>>(
                new Action<KeyDownEvent>(e =>
                {
                    if (e.keyCode != KeyCode.Escape || _squadDialog == null || _squadDialog.parent == null ||
                        _squadBatch != null)
                        return;
                    e.StopPropagation();
                    CancelSquadScan();
                })), TrickleDown.TrickleDown);
        }

        private void CheckPortraitTrigger()
        {
            var root = CurrentRoot();
            var header = FindNamed(root, "ReportHeader");
            if (header == null || header.worldBound.width < 800 ||
                !HeaderId(header, out long personId))
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
            if (picture.Q<VisualElement>("regeneratefm-portrait-trigger") != null)
                return;
            var trigger = new Button((Il2CppSystem.Action)(System.Action)(() => ShowPortraitMenu()))
            {
                name = "regeneratefm-portrait-trigger",
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
            Log.LogInfo("RegenerateFM portrait click enabled for " + personId);
        }

        private void ShowPortraitMenu()
        {
            var root = CurrentRoot();
            var header = FindNamed(root, "ReportHeader");
            var picture = FindNamed(header, "PersonPicture");
            if (root == null || header == null || picture == null ||
                !HeaderId(header, out long personId))
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
                name = "regeneratefm-portrait-dismiss",
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
            var menu = new VisualElement { name = "regeneratefm-portrait-menu" };
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
            menu.Add(PortraitMenuItem("regeneratefm-portrait-generate", "Generate new face", personId, false, true));
            menu.Add(PortraitMenuItem("regeneratefm-portrait-update", "Update face", personId, true, hasFace));
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
                if (currentHeader == null || !HeaderId(currentHeader, out long currentId) ||
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
            if (!HeaderId(header, out long personId))
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

        // Each shown text with the names of the elements above it, up to the given one.
        private static string DescribeTexts(VisualElement top)
        {
            var parts = new List<string>();
            void Visit(VisualElement element, string path)
            {
                if (element == null || element.worldBound.width <= 0 || element.worldBound.height <= 0 || parts.Count >= 60)
                    return;
                if (!string.IsNullOrEmpty(element.name))
                    path = path.Length == 0 ? element.name : path + "/" + element.name;
                var text = element.TryCast<TextElement>();
                if (text != null)
                {
                    var value = WebUtility.HtmlDecode(Tags.Replace(text.text ?? "", "")).Trim();
                    if (value.Length > 0)
                        parts.Add(path + " = \"" + value + "\"");
                }
                for (int i = 0; i < element.hierarchy.childCount; i++)
                    Visit(element.hierarchy.ElementAt(i), path);
            }
            Visit(top, "");
            return string.Join("; ", parts);
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
                var previous = picture.Q<VisualElement>("regeneratefm-busy-notice");
                if (previous != null)
                    previous.RemoveFromHierarchy();
                var notice = new VisualElement { name = "regeneratefm-busy-notice", pickingMode = PickingMode.Ignore };
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

        // FM26 shows a silhouette when a person has no face (neither a generated
        // one nor a game photo): "person_default", or a variant of it.
        private static bool IsDefaultPortrait(string address) =>
            address != null && address.StartsWith("person_default", StringComparison.Ordinal);

        // FM26 shows "person_default" (a silhouette) when a person has no face:
        // neither a generated one nor a game photo.
        private static bool HasFace(VisualElement picture) => FaceState(picture) == true;

        // True or false once the portrait has loaded; null while it is still loading.
        private static bool? FaceState(VisualElement picture)
        {
            var image = picture == null ? null : FindNamed(picture, "PersonImage")?.TryCast<SI.UI.Image>();
            var address = image?.m_imageAddressableID?.ToString();
            return string.IsNullOrEmpty(address) ? (bool?)null : !IsDefaultPortrait(address);
        }

        // An Actions menu choice: act on the profile open now. ``expected`` is the
        // ID the header showed when the menu opened, if it showed one yet.
        private void GenerateOpenProfile(long? expected, bool update)
        {
            var root = CurrentRoot();
            var header = FindNamed(root, "ReportHeader");
            if (header == null || !HeaderId(header, out long personId))
            {
                Log.LogWarning("The profile's ID could not be read yet; no image request was made");
                ShowBusyNotice("The profile is still loading. Choose it again.");
                return;
            }
            if (expected.HasValue && expected.Value != personId)
            {
                Log.LogWarning("The profile changed after the Actions menu opened; no image request was made");
                return;
            }
            bool staff = !IsPlayerOverview(root) && !IsPlayerHeader(header);
            Generate(personId, staff, update);
        }

        // The face FM26 is showing, as a PNG, for "Update face". The generator
        // prefers RegenerateFM's own saved file when this is a generated face.
        private byte[] CaptureReferencePng(VisualElement picture)
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
                return Png.Encode(rgba, width, height, bottomUp: true);
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

        private static VisualElement BuildOverlay(VisualElement picture, out Label message)
        {
            if (picture == null)
                throw new InvalidOperationException("FM26 player picture was not found.");
            var previous = picture.Q<VisualElement>("regeneratefm-generating-overlay");
            if (previous != null)
                previous.RemoveFromHierarchy();
            var overlay = new VisualElement { name = "regeneratefm-generating-overlay", pickingMode = PickingMode.Ignore };
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
            public bool KeyRejected;
            public bool OutOfCredits;
        }

        private static async Task<GenerationResult> RequestPortrait(long playerId, ProfileSnapshot profile,
            ImageSettings settings, string mode = "generate", byte[] shownPng = null)
        {
            try
            {
                string folder = GraphicsFolder();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                await FaceGenerator.MakePortrait(settings, folder, playerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                                 profile, mode == "update", shownPng).ConfigureAwait(false);
                Instance?.Log.LogInfo("Image request and save took " + timer.Elapsed.TotalSeconds.ToString("0.0") + " s");
                return new GenerationResult { Success = true, GraphicsDir = folder };
            }
            catch (ApiKeyRejectedException error)
            {
                return new GenerationResult { Error = error.Message, KeyRejected = true };
            }
            catch (OutOfCreditsException error)
            {
                return new GenerationResult { Error = error.Message, OutOfCredits = true };
            }
            catch (Exception error)
            {
                return new GenerationResult { Error = error.Message };
            }
        }

        // FM26's user-data folder is in Documents on Windows and in Application Support on macOS.
        private static string GraphicsFolder()
        {
            string userData = OperatingSystem.IsMacOS()
                ? KeyStore.ApplicationSupport
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(userData, "Sports Interactive", UserDataName, "graphics", "regeneratefm");
        }

        // The game's folder under Sports Interactive; SupportedGameBuild sets it for the Demo.
        private static string UserDataName = "Football Manager 26";

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
                Log.LogWarning("RegenerateFM graphics folder or config.xml was not found: " + folder);
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
                _lastLocatorUtc = null;
                _refreshStartedUtc = System.DateTime.UtcNow;
                resources[0].RegisterLocalPath(folder, null);
                Log.LogInfo("Requested RegenerateFM graphics reload from " + folder);
                return true;
            }
            catch (Exception error)
            {
                WaitingForGraphics = false;
                Log.LogError("Could not register RegenerateFM graphics: " + error);
                return false;
            }
        }

        private void CheckRefreshTimeout()
        {
            if (WaitingForGraphics && _lastLocatorUtc.HasValue &&
                System.DateTime.UtcNow - _lastLocatorUtc.Value >= LocatorsSettled)
            {
                WaitingForGraphics = false;
                _lastLocatorUtc = null;
                Log.LogInfo("RegenerateFM graphics registered after " +
                            (System.DateTime.UtcNow - _refreshStartedUtc).TotalSeconds.ToString("0.0") + " s");
                GraphicsLoaded();
                return;
            }
            if (!WaitingForGraphics || System.DateTime.UtcNow - _refreshStartedUtc < GraphicsTimeout)
                return;
            WaitingForGraphics = false;
            ShowError(_refreshOverlay, _refreshMessage,
                "Portrait saved, but graphics reload timed out. Restart FM26 to load it.");
            _refreshOverlay = null;
            _refreshMessage = null;
            Log.LogWarning("RegenerateFM graphics reload timed out after 30 seconds; refresh lock released");
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
                Log.LogInfo("RegenerateFM graphics loaded and panels refreshed");
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

        // EnumerateLocalAssetsComplete takes an AsyncOperationHandle struct, which
        // Il2CppInterop's hook passes with the Windows calling convention. Under
        // macOS's x64 System V convention the game then gets a broken handle,
        // throws "Attempting to use an invalid operation handle" and never
        // finishes the reload, so there that method isn't hooked. FM26 calls
        // RegisterResourceLocator (object arguments only) for each file of the
        // folder as the enumeration completes; once those calls have stopped for
        // LocatorsSettled, CheckRefreshTimeout refreshes the panels.
        [HarmonyPatch(typeof(ResourcesSubsystem), "RegisterResourceLocator")]
        private static class LocatorPatch
        {
            private static void Postfix()
            {
                if (WaitingForGraphics)
                    _lastLocatorUtc = System.DateTime.UtcNow;
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
                    // The ID is read again on click: just after a profile opens the
                    // header can be drawn before its data is bound.
                    if (header == null || !IsActionTarget(context.Target, header))
                        return;
                    var contributor = Instance._menuContributor.TryCast<IMenuContributor>();
                    context.AddContributor(contributor);
                }
                catch (Exception error)
                {
                    Instance.Log.LogWarning("Could not add RegenerateFM Actions item: " + error);
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
                        Instance.Log.LogInfo("RegenerateFM actions moved to bottom of Actions menu");
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
                if (header == null || !IsActionTarget(context.Target, header))
                    return;
                long? personId = HeaderId(header, out long shownId) ? shownId : (long?)null;
                context.Description.AddItem(parent, "Generate face",
                    (Il2CppSystem.Action)(System.Action)(() => Instance.GenerateOpenProfile(personId, false)));
                var update = context.Description.AddItem(parent, "Update face",
                    (Il2CppSystem.Action)(System.Action)(() => Instance.GenerateOpenProfile(personId, true)));
                // Updating needs a face to start from: a generated one or the game's photo.
                // While the picture is still loading, leave it enabled; the click checks again.
                if (FaceState(FindNamed(header, "PersonPicture")) == false)
                    context.Description.SetEnabled(update, false);
                _bound = true;
                Instance.Log.LogInfo("RegenerateFM actions contributed to the Actions menu for " +
                                     (personId?.ToString() ?? "a profile still loading"));
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
