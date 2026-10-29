using System;
using System.IO;
using System.Text;
using BepInEx;
using SI.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace RegenerateFM.FM26
{
    // A developer's aid for a new FM version, whose screens may have changed:
    // creating BepInEx/regeneratefm-dump-ui writes the UI documents' shown
    // elements (name, type, text, size) to BepInEx/regeneratefm-ui.txt, and the
    // flag is removed. Nothing happens without the flag.
    public sealed partial class Plugin
    {
        internal void CheckUiDumpFlag()
        {
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
