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
using UnityEngine.ResourceManagement.ResourceLocations;

namespace RegenerateFM.FM26
{
    // Harmony patches into FM26, and the Actions menu items.
    public sealed partial class Plugin
    {
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
        // finishes the reload, so there that method isn't hooked; FM27 has no
        // such method. Both games call RegisterResourceLocator (object arguments
        // only) for each file of the folder as the enumeration completes; once
        // those calls have stopped for LocatorsSettled, CheckRefreshTimeout
        // refreshes the panels.
        [HarmonyPatch(typeof(ResourcesSubsystem), "RegisterResourceLocator")]
        private static class LocatorPatch
        {
            private static void Postfix()
            {
                if (WaitingForGraphics)
                    _lastLocatorUtc = System.DateTime.UtcNow;
            }
        }

        // FM26 keeps one picture of each type for a key (person_<id>), the last
        // one registered. Our face for someone who also has a picture in
        // another graphics folder (a facepack) replaces that picture, and FM26
        // registers those folders only at startup, so once DropLocalKeys takes
        // our face out FM26 would show a silhouette until a restart. The
        // replaced picture is kept for DropLocalKeys to put back. Not used
        // where FM has PurgeLocalPath (FM27).
        [HarmonyPatch(typeof(ResourcesSubsystem), "RegisterResourceLocator")]
        private static class ReplacedPicturePatch
        {
            private static bool _failed;

            private static void Prefix(ResourcesSubsystem __instance, Il2CppSystem.Object __0, IResourceLocation __1)
            {
                if (_failed)
                    return;
                try
                {
                    // Every picture FM registers comes here, tens of thousands with
                    // a facepack: those not in our folder are passed over first.
                    if (__0 == null || !InFolder(__1?.InternalId, _ourFolder))
                        return;
                    var map = __instance.m_resourceMap;
                    if (map == null || !map.ContainsKey(__0))
                        return;
                    var list = map[__0];
                    for (int i = 0; i < list.Count; i++)
                    {
                        var old = list[i];
                        // FM finds its own photos and 3D faces again by itself.
                        if (old != null && old.ProviderId == LocalProvider && !InFolder(old.InternalId, _ourFolder))
                            ReplacedPictures[__0.ToString()] = old;
                    }
                }
                catch (Exception error)
                {
                    _failed = true;
                    Instance.Log.LogWarning("Could not keep FM26's pictures from other graphics folders: " + error);
                }
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
                    {
                        // Why a menu got no face commands, for a game whose screens have changed.
                        Instance.Log.LogDebug("Not a profile's Actions menu (header " + (header == null ? "not found" : "found") +
                                              "): " + Ancestry(context.Target));
                        return;
                    }
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
                if (menu == null)
                    return;
                // The contributor's rows are drawn at once, while FM adds its own over
                // the next frames. Keep them hidden until FM has stopped adding rows
                // (or 2 s have passed), then show them at the bottom.
                var opened = System.DateTime.UtcNow;
                var lastChange = opened;
                int rows = -1;
                bool shown = false;
                // Found once: a hidden row has no size, so FindText can't find it again.
                var ours = new VisualElement[MenuLabels.Length];
                IVisualElementScheduledItem check = null;
                check = menu.schedule.Execute((Il2CppSystem.Action)(System.Action)(() =>
                {
                    var now = System.DateTime.UtcNow;
                    for (int i = 0; i < ours.Length; i++)
                        ours[i] = ours[i] ?? MenuRow(menu, Text.T(MenuLabels[i]));
                    var view = ours[0]?.parent;
                    if (Array.Exists(ours, row => row == null || row.parent != view))
                    {
                        if (now - opened > MenuSettle + MenuSettle)
                            check?.Pause();
                        return;
                    }
                    if (view.childCount != rows)
                    {
                        rows = view.childCount;
                        lastChange = now;
                    }
                    bool settled = now - lastChange >= MenuSettle || now - opened >= MenuGiveUp;
                    if (!settled && !shown)
                    {
                        foreach (var row in ours)
                            row.style.display = DisplayStyle.None;
                        return;
                    }
                    bool last = true;
                    for (int i = 0; i < ours.Length; i++)
                        last &= view.IndexOf(ours[i]) == view.childCount - ours.Length + i;
                    if (!last)
                        foreach (var row in ours)
                            row.BringToFront();
                    if (!shown)
                    {
                        foreach (var row in ours)
                            row.style.display = DisplayStyle.Flex;
                        shown = true;
                        Instance.Log.LogInfo("RegenerateFM actions shown at the bottom of the Actions menu after " +
                                             (now - opened).TotalSeconds.ToString("0.00") + " s");
                    }
                    // FM can still add rows after ours appear; keep ours last a little longer.
                    if (now - opened >= MenuGiveUp)
                        check?.Pause();
                }));
                check.Every(0);
            }

            // RegenerateFM's rows, in their order at the bottom of the menu.
            private static readonly string[] MenuLabels = { "Generate face", "Update face", "Previous face", "Remove face" };

            // How long FM's own rows must stop arriving before ours are shown, and
            // the longest the menu waits for that.
            private static readonly TimeSpan MenuSettle = TimeSpan.FromMilliseconds(250);
            private static readonly TimeSpan MenuGiveUp = TimeSpan.FromSeconds(2);

            private static VisualElement MenuRow(VisualElement menu, string text)
            {
                var row = FindText(menu, text)?.parent?.parent;
                return row?.parent?.name == "View" ? row : null;
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
                {
                    Instance.Log.LogDebug("No face commands for this menu (header " + (header == null ? "not found" : "found") +
                                          "): " + Ancestry(context.Target));
                    return;
                }
                long? personId = HeaderId(header, out long shownId) ? shownId : (long?)null;
                context.Description.AddItem(parent, Text.T("Generate face"),
                    (Il2CppSystem.Action)(System.Action)(() => Instance.GenerateOpenProfile(personId, false)));
                var update = context.Description.AddItem(parent, Text.T("Update face"),
                    (Il2CppSystem.Action)(System.Action)(() => Instance.GenerateOpenProfile(personId, true)));
                // Updating needs a face to start from: a generated one or the game's photo.
                // While the picture is still loading, leave it enabled; the click checks again.
                if (FaceState(FindNamed(header, "PersonPicture")) == false)
                    context.Description.SetEnabled(update, false);
                var previous = context.Description.AddItem(parent, Text.T("Previous face"),
                    (Il2CppSystem.Action)(System.Action)(() => Instance.ChangeFace(personId, false)));
                var remove = context.Description.AddItem(parent, Text.T("Remove face"),
                    (Il2CppSystem.Action)(System.Action)(() => Instance.ChangeFace(personId, true)));
                var choices = personId.HasValue ? FaceChoices(personId.Value, ShownName(header)) : (Previous: true, Remove: true);
                if (!choices.Previous)
                    context.Description.SetEnabled(previous, false);
                if (!choices.Remove)
                    context.Description.SetEnabled(remove, false);
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
                if (element.worldBound.width < 250 && TextValues(element).Exists(value => IsLabel(value, "Actions", ActionsLabel)))
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
