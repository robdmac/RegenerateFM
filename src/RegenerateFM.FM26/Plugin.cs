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
    [BepInPlugin("fm.regenerate", "RegenerateFM", "1.0.0")]
    public sealed partial class Plugin : BasePlugin
    {
        internal static Plugin Instance;
        internal static bool WaitingForGraphics;
        // Rich-text tags, and the zero-width spaces FM26 puts between Japanese and
        // Chinese words as line-break hints ("ミッド​フィル​ダー").
        private static readonly Regex Tags = new Regex("<[^>]*>|​", RegexOptions.Compiled);
        // "16 years old" in the full-size profile header, "16 y/o (3/26/2008)" in
        // the compact one FM26 shows on a smaller screen.
        private static readonly Regex AgePattern = new Regex(@"\b(\d{1,2}) (?:years old|y/o)\b", RegexOptions.Compiled);
        private static readonly Regex IdPattern = new Regex(@"\bID:\s*(\d{1,20})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StatPattern = new Regex(@"^(?:[1-9]|1[0-9]|20)(?:[-–](?:[1-9]|1[0-9]|20))?$", RegexOptions.Compiled);
        // One position or several, comma-separated: "Attacking Midfielder (Right), Striker (Centre)".
        private const string OnePosition = @"(?:Goalkeeper|Defender|Wing[- ]Back|Defensive Midfielder|Midfielder|Attacking Midfielder|Striker)(?:\s*\([^)]{1,30}\))?";
        private static readonly Regex PositionPattern = new Regex("^" + OnePosition + @"(?:\s*,\s*" + OnePosition + ")*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // The compact header's abbreviations: "M/AM (C)", "D (RL), DM".
        private static readonly Regex ShortPositionPattern = new Regex(@"^(?:GK|(?:D|WB|DM|M|AM|ST)(?:/(?:D|WB|DM|M|AM|ST))*(?: \([RLC]{1,3}\))?)(?:, (?:GK|(?:D|WB|DM|M|AM|ST)(?:/(?:D|WB|DM|M|AM|ST))*(?: \([RLC]{1,3}\))?))*$", RegexOptions.Compiled);
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
        // RegenerateFM credits sign-in in progress (Credits): starting, then polling.
        private Task<Credits.SignIn> _creditsStart;
        private Credits.SignIn _creditsSignIn;
        private string _creditsService;  // the service the sign-in started with
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
        // How long a staff list name has to open its profile before the batch
        // takes it as not a link.
        private static readonly TimeSpan StaffOpenWait = TimeSpan.FromSeconds(8);
        // A click on a list that has only just come back (after Back) can be
        // lost, so a name that hasn't opened is clicked again after this, twice.
        private static readonly TimeSpan StaffClickAgain = TimeSpan.FromSeconds(2.5);
        private const int StaffClicks = 3;

        public override void Load()
        {
            if (!SupportedGameBuild())
            {
                Log.LogWarning("RegenerateFM supports only the checked FM26 26.3.2 and FM26 Demo binaries; buttons disabled");
                return;
            }
            Instance = this;
            ClassInjector.RegisterTypeInIl2Cpp<FaceMenuContributor>(new RegisterTypeOptions
            {
                Interfaces = new[] { typeof(IMenuContributor) }
            });
            _menuContributor = new FaceMenuContributor();
            _harmony = new Harmony("fm.regenerate");
            // See LocatorPatch: on macOS the reload is followed another way.
            if (OperatingSystem.IsMacOS())
                _harmony.PatchAll(typeof(LocatorPatch));
            else
                _harmony.PatchAll(typeof(AssetLoadPatch));
            _harmony.PatchAll(typeof(MenuOpenPatch));
            AddComponent<StaffWatcher>();
            Log.LogInfo("RegenerateFM Actions and portrait menu integration loaded");
        }

        // The key or credits token (KeyStore) and settings.ini (Settings), both
        // reread before each request.
        private ImageSettings CurrentSettings()
        {
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
            return Settings.Load(Settings.DefaultPath).ForKey(key);
        }

        // ArgumentException, with the message for the key dialog, when settings.ini can't be used.
        private static string CreditsServiceUrl()
        {
            try
            {
                return Settings.Load(Settings.DefaultPath).CreditsServiceUrl;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new ArgumentException("Could not read " + Settings.DefaultPath + ": " + error.Message);
            }
        }
    }
}
