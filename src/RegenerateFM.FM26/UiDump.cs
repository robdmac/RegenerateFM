using System;
using System.IO;
using System.Text;
using BepInEx;
using SI.Translation;
using SI.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace RegenerateFM.FM26
{
    // A developer's aid for a new FM version, whose screens may have changed:
    // creating BepInEx/regeneratefm-dump-ui writes the UI documents' shown
    // elements (name, type, text, size, text ID, binding, image) to
    // BepInEx/regeneratefm-ui.txt, and the flag is removed. Creating
    // BepInEx/regeneratefm-dump-texts writes FM's text for every text ID in the
    // player's language to BepInEx/regeneratefm-texts-<language>.txt: the
    // attribute name IDs (AttributeNames.TextIds) were found with it. Nothing
    // happens without a flag.
    public sealed partial class Plugin
    {
        internal void CheckUiDumpFlag()
        {
            string textsFlag = Path.Combine(Paths.BepInExRootPath, "regeneratefm-dump-texts");
            if (File.Exists(textsFlag))
            {
                File.Delete(textsFlag);
                var started = DateTime.UtcNow;
                var all = new StringBuilder();
                int count = 0;
                // FM27 27.0.1's IDs end below 50,000; a few seconds' search.
                for (int id = 1; id < 1_000_000; id++)
                {
                    string value;
                    try { value = new TranslationID(id, null).TranslatedString; }
                    catch (Exception) { continue; }
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    count++;
                    all.Append(id).Append('\t').Append(value.Replace("\r", " ").Replace("\n", " ")).Append('\n');
                }
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "regeneratefm-texts-" + Text.Language + ".txt"), all.ToString());
                Log.LogInfo("Wrote " + count + " texts in " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.0") + " s");
            }
            string flag = Path.Combine(Paths.BepInExRootPath, "regeneratefm-dump-ui");
            if (!File.Exists(flag))
                return;
            File.Delete(flag);
            var text = new StringBuilder();
            var found = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<UIDocument>());
            text.Append("UIDocuments: ").Append(found?.Length ?? -1)
                .Append(", generic lookup: ").Append(Resources.FindObjectsOfTypeAll<UIDocument>()?.Length ?? -1).Append('\n');
            foreach (var item in found)
            {
                var document = item.Cast<UIDocument>();
                text.Append("UIDocument ").Append(document.name).Append('\n');
                DumpElement(document.rootVisualElement, 1, text);
            }
            File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "regeneratefm-ui.txt"), text.ToString());
            Log.LogInfo("Wrote the UI tree to regeneratefm-ui.txt");
        }

        private static void DumpElement(VisualElement element, int depth, StringBuilder text)
        {
            if (element == null || depth > 60)
                return;
            var bounds = element.worldBound;
            if (bounds.width <= 0 && depth > 2)
                return;
            text.Append(' ', depth * 2).Append(element.name ?? "?").Append(" : ").Append(element.GetIl2CppType().Name);
            var label = element.TryCast<TextElement>();
            if (label != null && !string.IsNullOrEmpty(label.text))
                text.Append(" \"").Append(label.text.Replace("\n", " ")).Append('"');
            text.Append(FormattableString.Invariant($" [{bounds.x:0},{bounds.y:0} {bounds.width:0}x{bounds.height:0}]"));
            // FM's text ID (the same in every language) and what the text is bound to.
            if (LabelId(element) is int id && id != 0)
                text.Append(" id=").Append(id);
            if (BindingName(element) is string binding)
                text.Append(" bound=").Append(binding);
            // A portrait's image: "newgen_..." is FM's generated face (IsNewgenPortrait).
            if (element.TryCast<SI.UI.Image>()?.m_imageAddressableID?.ToString() is string image && image.Length > 0)
                text.Append(" image=").Append(image);
            var remapper = element.TryCast<SI.Bindable.BindingRemapper>();
            if (remapper?.Mappings != null)
                foreach (var mapping in remapper.Mappings)
                    text.Append(" {").Append(mapping.from).Append(" -> ").Append(mapping.to).Append('}');
            text.Append('\n');
            for (int i = 0; i < element.hierarchy.childCount; i++)
                DumpElement(element.hierarchy.ElementAt(i), depth + 1, text);
        }
    }
}
