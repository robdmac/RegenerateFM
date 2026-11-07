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
    // The portrait's own menu (Generate new face, Update face) and finding things on screen.
    public sealed partial class Plugin
    {
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
            var choices = FaceChoices(personId, ShownName(header));
            menu.Add(PortraitMenuItem("regeneratefm-portrait-generate", Text.T("Generate new face"), personId, false, true));
            menu.Add(PortraitMenuItem("regeneratefm-portrait-update", Text.T("Update face"), personId, true, hasFace));
            menu.Add(PortraitMenuItem("regeneratefm-portrait-previous", Text.T("Previous face"), personId, false, choices.Previous,
                                      () => ChangeFace(personId, false), Text.T("No earlier face to go back to")));
            menu.Add(PortraitMenuItem("regeneratefm-portrait-remove", Text.T("Remove face"), personId, false, choices.Remove,
                                      () => ChangeFace(personId, true), Text.T("No RegenerateFM face to remove")));
            root.Add(Directed(menu));
            _portraitDismissLayer = dismissLayer;
            _portraitMenu = menu;
            _portraitMenuPersonId = personId;
        }

        // ``action`` replaces Generate (or Update) face; ``disabledTip`` explains a disabled item.
        private Button PortraitMenuItem(string name, string text, long personId, bool update, bool enabled,
                                        Action action = null, string disabledTip = null)
        {
            var item = new Button((Il2CppSystem.Action)(System.Action)(() =>
            {
                ClosePortraitMenu();
                if (action != null)
                {
                    action();
                    return;
                }
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
            // While a face is being made or loaded, every item waits for it.
            bool busy = FaceBusy;
            if (busy)
                enabled = false;
            item.style.width = Length.Percent(100);
            item.style.height = 32;
            item.style.color = enabled ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);
            item.style.backgroundColor = new Color(0.19f, 0.15f, 0.25f, 1f);
            item.style.unityTextAlign = TextAnchor.MiddleLeft;
            item.style.paddingLeft = 10;
            item.SetEnabled(enabled);
            if (!enabled)
                item.tooltip = busy ? Text.T("Face generation is already in progress")
                                    : disabledTip ?? Text.T("No face to update yet");
            return item;
        }

        private bool FaceBusy => _pending != null || _squadBatch != null || WaitingForGraphics;

        private void ClosePortraitMenu()
        {
            if (_portraitMenu != null && _portraitMenu.parent != null)
                _portraitMenu.RemoveFromHierarchy();
            if (_portraitDismissLayer != null && _portraitDismissLayer.parent != null)
                _portraitDismissLayer.RemoveFromHierarchy();
            _portraitMenu = null;
            _portraitDismissLayer = null;
        }

        // Reading a page's texts for a biography is slow, so it is done every few
        // seconds, not while a squad scan or batch is working, and not again once
        // a person's biography is kept.
        private float _nextBiographyCheck;

        private void CheckBiographyCache()
        {
            if (Time.unscaledTime < _nextBiographyCheck || _squadScan != null || _squadBatch != null)
                return;
            _nextBiographyCheck = Time.unscaledTime + 3f;
            var root = CurrentRoot();
            if (root == null)
                return;
            var header = FindNamed(root, "ReportHeader");
            if (header == null || header.worldBound.width < 800 || FindNamed(header, "PersonPicture") == null)
                return;
            if (!HeaderId(header, out long personId) || _biographyCache.ContainsKey(personId))
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

        // The first shown element with this name. Walking FM26's tree from here
        // crosses into IL2CPP for every element, which on a busy page (your own
        // player's profile, Squad > Overview) took long enough to slow FM26's own
        // page loading many times over; UI Toolkit's Q walks it natively. Its
        // first match is the walk's answer when shown; a hidden one (FM26 keeps
        // some pages laid out but not shown) needs the walk.
        private static VisualElement FindNamed(VisualElement element, string name)
        {
            if (element == null)
                return null;
            var first = element.Q<VisualElement>(name);
            if (first == null)
                return null;
            if (first.worldBound.width > 0 && first.worldBound.height > 0)
                return first;
            return WalkNamed(element, name);
        }

        private static VisualElement WalkNamed(VisualElement element, string name)
        {
            if (element == null)
                return null;
            if (element.name == name && element.worldBound.width > 0 && element.worldBound.height > 0)
                return element;
            for (int i = 0; i < element.hierarchy.childCount; i++)
            {
                var found = WalkNamed(element.hierarchy.ElementAt(i), name);
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

        // The shown text of each link in the element, in order.
        private static List<string> LinkTexts(VisualElement element)
        {
            var values = new List<string>();
            void Visit(VisualElement item)
            {
                if (item == null || item.worldBound.width <= 0 || item.worldBound.height <= 0)
                    return;
                if (item.GetIl2CppType().Name == "NavigableLink")
                {
                    if (FirstText(item) is string value)
                        values.Add(value);
                    return;
                }
                for (int i = 0; i < item.hierarchy.childCount; i++)
                    Visit(item.hierarchy.ElementAt(i));
            }
            Visit(element);
            return values;
        }
    }
}
