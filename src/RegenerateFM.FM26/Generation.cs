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
    // One face: Generate or Update from a profile, the request, the portrait overlay and
    // reloading FM26's graphics.
    public sealed partial class Plugin
    {
        // ethnicityAsked: the user has answered the ethnicity question, with
        // chosenEthnicity (one of Prompts.Ethnicities) or null for a guess.
        private void Generate(long? personId, bool staff, bool update = false, bool ethnicityAsked = false,
                              string chosenEthnicity = null)
        {
            CheckRefreshTimeout();
            PollGeneration();
            if (_pending != null || _squadBatch != null)
            {
                Log.LogWarning("A portrait request is already in progress");
                ShowBusyNotice(Text.T("Face generation is already in progress"));
                return;
            }
            if (WaitingForGraphics)
            {
                Log.LogWarning("A portrait is still loading into FM26; wait for graphics refresh before generating again");
                ShowBusyNotice(Text.T("Waiting for the previous face to load"));
                return;
            }
            if (!personId.HasValue)
            {
                Log.LogWarning("Could not identify the current person; no image request was made");
                return;
            }
            if (!KeyReady(() => Generate(personId, staff, update, ethnicityAsked, chosenEthnicity)))
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
                bool newgenFace = false;
                if (update)
                {
                    if (!HasFace(picture))
                        throw new InvalidOperationException(Text.T("This person has no face to update yet. Use Generate face instead."));
                    reference = CaptureReferencePng(picture);
                    newgenFace = IsNewgenPortrait(picture);
                }
                else
                {
                    // A real person gets their own likeness; only a newgen without
                    // FM's look for them needs the user's say on their ethnicity.
                    bool known = FindKnownEthnicity(personId.Value, picture, snapshot, out bool newgen);
                    snapshot.Likeness = !newgen;
                    if (!known && newgen)
                    {
                        if (!ethnicityAsked)
                        {
                            AskEthnicity(personId.Value, staff, snapshot.Name);
                            return;
                        }
                        if (chosenEthnicity != null)
                        {
                            snapshot.Ethnicity = chosenEthnicity;
                            SaveEthnicities(new Dictionary<string, RegenerateFM.KnownEthnicity>
                            {
                                [personId.Value.ToString(CultureInfo.InvariantCulture)] =
                                    new RegenerateFM.KnownEthnicity { Ethnicity = chosenEthnicity, Source = "chosen" }
                            });
                        }
                    }
                }
                overlay = BuildOverlay(picture, out message);
                Log.LogInfo((update ? "Updating " + (newgenFace ? "FM newgen face for " : "face for ") : "Generating face for ") +
                            snapshot.Name + " (" + personId.Value + "): " +
                            snapshot.Physical.Count + " physical, " + snapshot.Technical.Count + " technical, " +
                            snapshot.Mental.Count + " mental, " + snapshot.Goalkeeping.Count + " goalkeeping attributes; bio=" +
                            (snapshot.BiographyHeadline != null) + "; family: " +
                            (FamilyText(snapshot) ?? "none") + (update ? "" : "; ethnicity: " + (snapshot.Ethnicity ?? (snapshot.Likeness ? "not known" : "guessed")) +
                                                                (snapshot.Likeness ? "; likeness" : "")));
                _pending = new PendingGeneration
                {
                    Task = RequestPortrait(personId.Value, snapshot, settings, update ? "update" : "generate", reference, newgenFace),
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
                    _scoutingRetry.Retry = () => Generate(personId, staff, update, ethnicityAsked, chosenEthnicity);
                    return;
                }
                _scoutingRetry = null;
                Log.LogInfo(error.Message);
                ShowDialog(error.Message, Text.T("Close"), () => CancelSquadScan());
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
                ShowError(pending.Overlay, pending.Message, Text.T("Portrait request timed out. Try again."));
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
                    pending.Message.text = Text.T("Loading face…");
                _refreshOverlay = pending.Overlay;
                _refreshMessage = pending.Message;
                if (!Refresh())
                {
                    ShowError(pending.Overlay, pending.Message,
                        Text.T("Portrait saved, but game refresh failed. Restart FM26 to load it."));
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
                picture.Add(Directed(notice));
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

        // FM26's own 3D face for a newgen ("newgen_<id>"), rather than a photo
        // ("person_<id>") or a RegenerateFM face.
        private static bool IsNewgenPortrait(VisualElement picture)
        {
            var address = FindNamed(picture, "PersonImage")?.TryCast<SI.UI.Image>()?.m_imageAddressableID?.ToString();
            return address != null && address.StartsWith("newgen_", StringComparison.Ordinal);
        }

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
                ShowBusyNotice(Text.T("The profile is still loading. Choose it again."));
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
                throw new InvalidOperationException(Text.T("FM26 player picture was not found."));
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
            message = new Label(Text.T("Generating {0}", "◐")) { pickingMode = PickingMode.Ignore };
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
                if (overlay.parent != null && !label.text.StartsWith(Text.T("Face generation failed")) &&
                    !label.text.StartsWith("Portrait saved") && label.text != Text.T("Loading face…"))
                    label.text = Text.T("Generating {0}", frames[frame++ % frames.Length]);
            })).Every(180);
            return Directed(overlay);
        }

        private static void ShowError(VisualElement overlay, Label message, string error)
        {
            if (overlay == null || message == null)
                return;
            message.text = Text.T("Face generation failed");
            overlay.tooltip = error ?? Text.T("Unknown error");
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
            ImageSettings settings, string mode = "generate", byte[] shownPng = null, bool shownNewgen = false)
        {
            try
            {
                string folder = GraphicsFolder();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                await Portraits.MakePortrait(settings, folder, playerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                                 profile, mode == "update", shownPng, shownNewgen: shownNewgen).ConfigureAwait(false);
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

        // ``purge``: a face was taken off, not replaced. FM27 keeps the folder's
        // old entries when it is registered again, so a removed face's name
        // still points at its missing file (a newgen then shows a blank instead
        // of FM's 3D face); the folder's entries are dropped first.
        private bool Refresh(bool purge = false)
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
                // By name: FM26 has no PurgeLocalPath.
                if (purge && typeof(ResourcesSubsystem).GetMethod("PurgeLocalPath") is System.Reflection.MethodInfo purgePath)
                {
                    purgePath.Invoke(resources[0], new object[] { folder });
                    Log.LogInfo("Dropped FM's entries for the RegenerateFM graphics folder");
                }
                // By name: FM27's returns a LocalPathScan, FM26's nothing, so a call
                // compiled against FM26's is a missing method in FM27.
                typeof(ResourcesSubsystem).GetMethod(nameof(ResourcesSubsystem.RegisterLocalPath))
                    .Invoke(resources[0], new object[] { folder, null });
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
                Text.T("Portrait saved, but graphics reload timed out. Restart FM26 to load it."));
            _refreshOverlay = null;
            _refreshMessage = null;
            Log.LogWarning("RegenerateFM graphics reload timed out after 30 seconds; refresh lock released");
        }

        // FM27's ReloadPanels leaves a profile's attributes block (an ExpandableContentElement,
        // which FM26 doesn't have) blank, its children hidden, until the next tab;
        // RefreshVisibleElements shows them. Called by name: the plugin builds against FM26.
        private static readonly System.TimeSpan RefillContentDelay = System.TimeSpan.FromSeconds(0.5);
        private System.DateTime? _refillContentUtc;

        private void RefillEmptyContent()
        {
            if (!_refillContentUtc.HasValue || System.DateTime.UtcNow < _refillContentUtc.Value)
                return;
            _refillContentUtc = null;
            var root = CurrentRoot();
            var type = System.Type.GetType("SI.Bindable.ExpandableContentElement, SI.Bindable");
            var refresh = type?.GetMethod("RefreshVisibleElements", System.Type.EmptyTypes);
            if (root == null || refresh == null)
                return;
            foreach (var element in AllElements(root).ToList())
            {
                if (element.GetIl2CppType().Name != "ExpandableContentElement" || element.worldBound.width <= 0)
                    continue;
                bool hidden = true;
                for (int i = 0; i < element.hierarchy.childCount && hidden; i++)
                    hidden = element.hierarchy.ElementAt(i).worldBound.width <= 0;
                if (hidden && element.hierarchy.childCount > 0)
                    refresh.Invoke(System.Activator.CreateInstance(type, element.Pointer), null);
            }
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
                if (Fm27)
                    _refillContentUtc = System.DateTime.UtcNow + RefillContentDelay;
                if (_refreshOverlay != null && _refreshOverlay.parent != null)
                    _refreshOverlay.RemoveFromHierarchy();
                Log.LogInfo("RegenerateFM graphics loaded and panels refreshed");
            }
            catch (Exception error)
            {
                ShowError(_refreshOverlay, _refreshMessage,
                    Text.T("Portrait saved, but panel refresh failed. Restart FM26 to load it."));
                Log.LogError("Graphics loaded, but panel refresh failed: " + error);
            }
            finally
            {
                _refreshOverlay = null;
                _refreshMessage = null;
            }
        }
    }
}
