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
                VisualElement generate = null, update = null;
                IVisualElementScheduledItem check = null;
                check = menu.schedule.Execute((Il2CppSystem.Action)(System.Action)(() =>
                {
                    var now = System.DateTime.UtcNow;
                    generate = generate ?? MenuRow(menu, "Generate face");
                    update = update ?? MenuRow(menu, "Update face");
                    if (generate == null || update == null || generate.parent != update.parent)
                    {
                        if (now - opened > MenuSettle + MenuSettle)
                            check?.Pause();
                        return;
                    }
                    var view = update.parent;
                    if (view.childCount != rows)
                    {
                        rows = view.childCount;
                        lastChange = now;
                    }
                    bool settled = now - lastChange >= MenuSettle || now - opened >= MenuGiveUp;
                    if (!settled && !shown)
                    {
                        generate.style.display = DisplayStyle.None;
                        update.style.display = DisplayStyle.None;
                        return;
                    }
                    if (view.IndexOf(update) != view.childCount - 1 || view.IndexOf(generate) != view.childCount - 2)
                    {
                        generate.BringToFront();
                        update.BringToFront();
                    }
                    if (!shown)
                    {
                        generate.style.display = DisplayStyle.Flex;
                        update.style.display = DisplayStyle.Flex;
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
