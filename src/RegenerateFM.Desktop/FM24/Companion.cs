using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RegenerateFM.Desktop.FM24.Bridge;
using RegenerateFM.Desktop.FM24.Memory;

namespace RegenerateFM.Desktop.FM24
{
    // In-game progress for one face: on RegenerateFM's portrait popup, or in a
    // RegenerateFM dialog for skins without it.
    public interface IFaceProgress
    {
        bool Failed { get; }
        void Loading();
        // Shows ``message``; false if the player must still be told some other way.
        bool Fail(string message);
        void Close();
    }

    // Progress text on RegenerateFM's portrait popup inside FM24. The popup is
    // 88 pixels wide, so the texts are one short word. The skin reload after a
    // new face rebuilds the popup, which clears it. The constructor throws
    // DialogUnavailableException if FM24 cannot show it.
    public sealed class PortraitProgress : IFaceProgress
    {
        private readonly int _port;
        private readonly string _token;

        public PortraitProgress(int port, string token, string text)
        {
            _port = port;
            _token = token;
            Dll.SetProgress(port, token, "portrait", text);
        }

        public bool Failed { get; private set; }

        public void Loading() => Set("Loading…");

        private void Set(string text)
        {
            try
            {
                Dll.SetProgress(_port, _token, "portrait", text);
            }
            catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException ||
                                          error is System.Net.Sockets.SocketException)
            {
            }
        }

        public bool Fail(string message)
        {
            Failed = true;
            Set("Failed");
            return false;
        }

        public void Close() => Set("");
    }

    // Progress in a RegenerateFM OK dialog, for skins without RegenerateFM's
    // portrait popup. The skin reload after a new face closes it; a failure
    // turns it into the error message. Closing it early doesn't stop the face.
    public sealed class DialogProgress : IFaceProgress
    {
        private readonly int _port;
        private readonly string _token;

        public DialogProgress(int port, string token, string text)
        {
            _port = port;
            _token = token;
            Dll.ShowDialog(port, token, "message", "RegenerateFM", text);
        }

        public bool Failed { get; private set; }

        public void Loading()
        {
            try
            {
                Dll.SetProgress(_port, _token, "dialog", "Loading the new face…");
            }
            catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException || error is SocketException)
            {
            }
        }

        public bool Fail(string message)
        {
            Failed = true;
            try
            {
                Dll.SetProgress(_port, _token, "finish", Dll.ClipText(message));
                return true;
            }
            catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException || error is SocketException)
            {
                return false;
            }
        }

        public void Close()
        {
        }
    }

    // The FM24 companion: reads the open profile from game memory and makes its
    // face. It needs RegenerateFM's native library in FM24 (the DLL on Windows
    // and Proton, libregeneratefm24.dylib on macOS), which shows its menus and
    // dialogs and reloads the skin.
    public sealed partial class Companion
    {
        public const string NoKeySaved = "No OpenAI key was saved, so no image request was made.";
        public const string MissingKey = "RegenerateFM has no OpenAI key or credits sign-in saved. Close and restart the companion to enter one.";
        public const string RejectedKey = " Close the companion and start it again to enter a new key.";
        // On macOS and Windows FM24 (or the key window) asks for the key itself (EnsureKey).
        public const string RejectedKeyInGame = " Choose the face command again to enter a new key.";

        private readonly object _lock = new object();
        private readonly object _keyLock = new object();
        private bool _inFlight;

        public string Token;
        public int ReloadPort;          // the DLL's port for dialogs, progress and reloads (bridge + 1)
        public string GraphicsDir;
        public string GameDir;
        public bool PreviewOnly;
        public int? StartedByFm24;      // FM24's process ID when the DLL started this companion
        public volatile bool KeyRejected;

        // Seams the tests replace.
        public Func<bool, (string PersonId, ProfileSnapshot Profile)> ReadProfile;
        public Func<string, IFaceProgress> StartProgress;
        public Func<ImageSettings, string, Task<byte[]>> GeneratePng = (settings, prompt) => ImageApi.GeneratePng(settings, prompt);
        public Func<ImageSettings, string, byte[], Task<byte[]>> EditPng = (settings, prompt, reference) => ImageApi.EditPng(settings, prompt, reference);
        public Action Reload;
        public Func<string, string, string, (byte[] Png, string Source)?> FindExistingPortrait = ExistingPortrait.Find;
        public Func<GameProcess> FindGame = People.FindGame;
        public Action<string> Log = text => Console.WriteLine(text);

        public Companion()
        {
            ReadProfile = ReadNativeProfile;
            StartProgress = StartInGameProgress;
            Reload = () => Dll.RequestReload(ReloadPort, Token);
        }

        // The image request settings for the saved key; MissingApiKeyException without one.
        private ImageSettings CurrentSettings() => RegenerateFM.Settings.Load(RegenerateFM.Settings.DefaultPath).ForKey(SavedKey());

        // The saved OpenAI key or credits token, or null.
        public string SavedKey()
        {
            try
            {
                return KeyStore.Load(KeyStore.DefaultPath);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException)
            {
                Log("Could not read the saved OpenAI key: " + error.Message);
                return null;
            }
        }

        // The error for FM24's dialog; a rejected key says how to replace it.
        private string ErrorText(Exception error)
        {
            if (!(error is ApiKeyRejectedException) && !(error is OutOfCreditsException))
                return error.Message;
            if (AsksInGame || AsksInWindow)
            {
                KeyRejected = true;
                return error.Message + RejectedKeyInGame;
            }
            return error.Message + RejectedKey;
        }

        private static bool Expected(Exception error) =>
            error is ArgumentException || error is InvalidOperationException || error is IOException ||
            error is InvalidDataException || error is MemoryUnavailableException || error is UnauthorizedAccessException ||
            error is System.Xml.XmlException;

        // In-game progress: on the RegenerateFM portrait popup, else in a
        // RegenerateFM dialog (another skin), else none.
        private IFaceProgress StartInGameProgress(string mode)
        {
            try
            {
                return new PortraitProgress(ReloadPort, Token, mode == "update" ? "Updating…" : "Generating…");
            }
            catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException ||
                                          error is System.Net.Sockets.SocketException)
            {
            }
            try
            {
                return new DialogProgress(ReloadPort, Token, mode == "update" ? "Updating this face…" : "Generating a new face…");
            }
            catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException ||
                                          error is System.Net.Sockets.SocketException)
            {
                Log("FM24 in-game progress unavailable (" + error.Message + "); continuing without it.");
                return null;
            }
        }

        // The open profile and the biography FM24 shows, from game memory only.
        // The open screen's header must show the person read from memory, and
        // both are read twice so navigation during capture is refused.
        // ``identityOnly`` reads just name, age and club.
        public (string, ProfileSnapshot) ReadNativeProfile(bool identityOnly)
        {
            GameProcess game;
            try
            {
                game = FindGame();
            }
            catch (MemoryUnavailableException error)
            {
                throw new InvalidOperationException(error.Message + " No image request was made.");
            }
            try
            {
                var screen = ScreenReader.Read(game);
                var (personId, profile) = ProfileReader.ReadOpenProfile(game, identityOnly, screen);
                if (screen.PersonId != personId)
                    throw new InvalidOperationException("The open FM24 screen shows " + screen.PersonId + ", not person " + personId +
                                                        "; no image request was made.");
                string biography = screen.Biography;
                if (!identityOnly && !string.IsNullOrEmpty(biography) &&
                    !biography.StartsWith("no biography is available", StringComparison.OrdinalIgnoreCase))
                    profile.BiographyHeadline = biography;
                // The check read uses the header and the known record: no memory search.
                var again = ProfileReader.RereadProfile(game, personId, screen.Title, profile.Age, identityOnly, screen.Today);
                if (!ScreenReader.Read(game).Equals(screen) || again.PersonId != personId ||
                    ProfileJson.Describe(again.Profile, withBiography: false) != ProfileJson.Describe(profile, withBiography: false))
                    throw new InvalidOperationException("The open FM24 profile changed during capture; no image request was made.");
                return (personId, profile);
            }
            catch (Exception error) when (error is MemoryUnavailableException || People.Unreadable(error))
            {
                throw new InvalidOperationException("Could not read the open FM24 profile from game memory (" + error.Message +
                                                    "); no image request was made.");
            }
        }

        // FM24's game folder, from the running game if it wasn't given.
        private string GameFolder()
        {
            if (GameDir != null)
                return GameDir;
            try
            {
                return ExistingPortrait.GameDirFromExecutable(FindGame().Executable);
            }
            catch (Exception error) when (error is MemoryUnavailableException || People.Unreadable(error))
            {
                return null;
            }
        }

        // Whether Update face has a portrait to start from (for the DLL's
        // popup). The DLL waits at most 30 ms, so the game archive is indexed
        // in the background on first use; until then there is no answer.
        public bool FaceExists(string personId)
        {
            GameDir ??= GameFolder();
            return ExistingPortrait.Has(personId, GraphicsDir, GameDir, wait: false);
        }

        // The FM24 the last reference was found in: finding it again lists
        // every process, about 4 ms of the 30 the menu's face check may take.
        private GameProcess _referenceGame;

        // The person ID for the object browser reference the Mac library sends
        // when the header shows no ID.
        public string ResolveReference(uint reference, bool wait)
        {
            var game = _referenceGame;
            if (game != null)
            {
                try
                {
                    return PersonHandles.PersonForReference(game, reference, wait);
                }
                catch (Exception error) when (error is MemoryUnavailableException || People.Unreadable(error))
                {
                    _referenceGame = null;
                }
            }
            try
            {
                game = FindGame();
                string person = PersonHandles.PersonForReference(game, reference, wait);
                _referenceGame = game;
                return person;
            }
            catch (MemoryUnavailableException error)
            {
                throw new InvalidOperationException(error.Message + " No image request was made.");
            }
        }

        // Finds FM24's person table once a game is loaded, checking every few
        // seconds, and makes one face check, so the Mac library's first one,
        // which names a person by reference, is answered within its 30 ms: the
        // first takes about 45 ms (finding FM24, the table's slot and compiling
        // the code that reads them), later ones about 1 ms.
        public void FindPersonTable()
        {
            while (true)
            {
                try
                {
                    // Handle 0 is the table's first person. Without the table a
                    // reference is looked for with a memory scan, so not before.
                    if (PersonHandles.TableRecord(FindGame(), 0) != null)
                    {
                        FaceExists(ResolveReference(FirstPerson, wait: false));
                        return;
                    }
                }
                catch (Exception error) when (error is MemoryUnavailableException || error is InvalidOperationException ||
                                              People.Unreadable(error))
                {
                }
                Thread.Sleep(5000);
            }
        }

        // The reference to the person with handle 0.
        private const uint FirstPerson = 0x01000000;

        // A face for the open profile, if it is still ``expectedId``: "generate"
        // a new one, or "update" the person's existing portrait.
        public FaceResult Generate(string expectedId, string mode)
        {
            lock (_lock)
            {
                if (_inFlight)
                    return FaceResult.Failed("A portrait is already being generated.");
                _inFlight = true;
            }
            IFaceProgress progress = null;
            try
            {
                if (!EnsureKey())
                    return new FaceResult { Cancelled = true, Error = NoKeySaved };
                if (!PreviewOnly)
                    progress = StartProgress(mode);
                var clock = Stopwatch.StartNew();
                var (playerId, profile) = ReadProfile(mode == "update");
                Log("Read " + profile.Name + " (" + playerId + ") from FM24's memory in " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s.");
                if (expectedId != null && expectedId != playerId)
                    throw new InvalidOperationException("The open FM24 profile is now person " + playerId + ", not the requested person " +
                                                        expectedId + "; no image request was made.");
                byte[] reference = null;
                string source = null, prompt;
                if (mode == "update")
                {
                    var found = FindExistingPortrait(playerId, GraphicsDir, GameFolder());
                    if (found == null)
                        throw new InvalidOperationException(profile.Name + " has no face to update yet. Choose Generate new face instead; " +
                                                            "no image request was made.");
                    (reference, source) = found.Value;
                    prompt = Prompts.UpdatePrompt(profile);
                }
                else
                    prompt = Prompts.ProfilePrompt(profile, playerId, PreviewOnly ? null : GraphicsDir);
                if (PreviewOnly)
                    return new FaceResult { Ok = true, Preview = true, PlayerId = playerId, Prompt = prompt, Reference = source };
                Portraits.CheckGraphicsConfig(GraphicsDir);
                var settings = RequestSettings();
                clock.Restart();
                var png = (reference != null ? EditPng(settings, prompt, reference) : GeneratePng(settings, prompt)).GetAwaiter().GetResult();
                Log("Image request took " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s.");
                return SaveAndReload(playerId, png, progress);
            }
            catch (Exception error) when (Expected(error) || error is MissingApiKeyException)
            {
                string text = ErrorText(error);
                return FaceResult.Failed(text, shown: progress != null && progress.Fail(text));
            }
            finally
            {
                if (progress != null && !progress.Failed)
                    progress.Close();
                lock (_lock)
                    _inFlight = false;
            }
        }

        // Saves the face and has FM24 reload it. The earlier face stays until the
        // reload succeeds: FM24 keeps showing it until then.
        private FaceResult SaveAndReload(string playerId, byte[] png, IFaceProgress progress)
        {
            string saved = Portraits.SavePortrait(GraphicsDir, playerId, png, removeEarlier: false);
            progress?.Loading();
            try
            {
                Reload();
            }
            catch (Exception error) when (Expected(error) || error is TimeoutException || error is System.Net.Sockets.SocketException)
            {
                string warning = "Portrait saved, but FM24 reload failed: " + error.Message + ". Use Preferences > Reload Skin.";
                // "Shown": the progress dialog now says so; nothing else needs to.
                return new FaceResult { Ok = true, PlayerId = playerId, Warning = warning, Shown = progress != null && progress.Fail(warning) };
            }
            Portraits.RemoveEarlier(saved, playerId);
            return new FaceResult { Ok = true, PlayerId = playerId, Refreshed = true };
        }

        // The settings for a request; MissingKey when there is no key at all.
        private ImageSettings RequestSettings()
        {
            try
            {
                return CurrentSettings();
            }
            catch (MissingApiKeyException)
            {
                throw new InvalidOperationException(MissingKey);
            }
        }

        // A face for a profile read by ID, by the squad batch, which reads each
        // player from memory instead of opening the profile. Shares the in-flight lock.
        public FaceResult GenerateForProfile(string playerId, ProfileSnapshot profile)
        {
            if (!EnsureKey())
                return new FaceResult { Cancelled = true, Error = NoKeySaved };
            lock (_lock)
            {
                if (_inFlight)
                    return FaceResult.Failed("A portrait is already being generated.");
                _inFlight = true;
            }
            try
            {
                string prompt = Prompts.ProfilePrompt(profile, playerId, PreviewOnly ? null : GraphicsDir);
                if (PreviewOnly)
                    return new FaceResult { Ok = true, Preview = true, PlayerId = playerId, Prompt = prompt };
                Portraits.CheckGraphicsConfig(GraphicsDir);
                return SaveAndReload(playerId, GeneratePng(RequestSettings(), prompt).GetAwaiter().GetResult(), null);
            }
            catch (Exception error) when (Expected(error) || error is MissingApiKeyException)
            {
                return FaceResult.Failed(ErrorText(error));
            }
            finally
            {
                lock (_lock)
                    _inFlight = false;
            }
        }
    }
}
