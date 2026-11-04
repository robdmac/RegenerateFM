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
    // FM's ethnicity for a person: from matches drawn in 3D, a newgen's appearance or the
    // user's choice.
    public sealed partial class Plugin
    {
        // ---- Ethnicity -------------------------------------------------------
        // FM's ethnicity, skin tone and hair colour for each person it draws in a
        // 3D match, kept by person ID in the faces folder (KnownEthnicities.FileName),
        // on this computer only. The match's 3D players keep their setup, which is
        // read every few seconds; hooking the setup call itself crashed FM26.
        private static readonly HashSet<int> MatchSeen = new HashSet<int>();
        private float _nextMatchScan;

        private void ReadMatchEthnicities()
        {
            if (Time.unscaledTime < _nextMatchScan)
                return;
            _nextMatchScan = Time.unscaledTime + 3f;
            var players = UnityEngine.Object.FindObjectsOfType<FM.Match.Player>();
            if (players == null || players.Length == 0)
                return;
            var found = new Dictionary<string, RegenerateFM.KnownEthnicity>();
            foreach (var player in players)
            {
                int id = player.ID;
                var visuals = id > 0 && !MatchSeen.Contains(id) ? player.Visuals3D : null;
                if (visuals == null)
                    continue;
                var setup = visuals.m_characterSetup;
                string ethnicity = setup.ethnicity.ToString(), skinTone = setup.skinTone.ToString();
                MatchSeen.Add(id);
                // FM's own colour, before HairColourName rounds it to one of ours,
                // so the palette can be checked against the colours FM uses.
                var hair = setup.hairColour;
                Log.LogInfo("Match hair colour for " + id + ": RGB " + string.Join(" ",
                    new[] { hair.r, hair.g, hair.b }.Select(part => part.ToString("0.000", CultureInfo.InvariantCulture))) +
                    ", named " + HairColourName(hair) + " (" + ethnicity + ", " + skinTone + ")");
                if (!Prompts.Ethnicities.Contains(ethnicity))
                    continue;
                found[id.ToString(CultureInfo.InvariantCulture)] = new RegenerateFM.KnownEthnicity
                {
                    Ethnicity = ethnicity,
                    SkinTone = Prompts.SkinTones.Contains(skinTone) ? skinTone : null,
                    HairColour = HairColourName(setup.hairColour),
                    Source = "match",
                };
            }
            if (found.Count > 0)
                SaveEthnicities(found);
        }

        private void SaveEthnicities(Dictionary<string, RegenerateFM.KnownEthnicity> people)
        {
            try
            {
                int changed = KnownEthnicities.Save(GraphicsFolder(), people);
                if (changed > 0)
                    Log.LogInfo("Saved the ethnicity of " + changed + (changed == 1 ? " person" : " people"));
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not save ethnicities: " + error.Message);
            }
        }

        // The match's hair colours, by the nearest of Prompts.HairColours.
        private static readonly (string Name, float R, float G, float B)[] HairPalette =
        {
            ("black", 0.17f, 0.18f, 0.18f), ("dark brown", 0.20f, 0.15f, 0.11f), ("light brown", 0.74f, 0.60f, 0.44f),
            ("blond", 0.81f, 0.71f, 0.50f), ("ginger", 0.83f, 0.38f, 0.14f), ("grey", 0.60f, 0.60f, 0.60f),
            ("white", 0.92f, 0.92f, 0.92f),
        };

        private static string HairColourName(Color colour) =>
            HairPalette.OrderBy(entry => (entry.R - colour.r) * (entry.R - colour.r) + (entry.G - colour.g) * (entry.G - colour.g) +
                                         (entry.B - colour.b) * (entry.B - colour.b)).First().Name;

        // A newgen's appearance as FM26 binds it for the 3D face: ethnicity 0-10
        // (Prompts.Ethnicities) and skin tone 1-20 (Prompts.SkinTones).
        // Real players hold a placeholder there, so it counts only while the
        // profile shows the newgen face.
        private static readonly Dictionary<long, int> NewgenEthnicityById = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> NewgenSkinToneById = new Dictionary<long, int>();
        private static readonly Dictionary<string, Bindings.ValueChangedCallback> NewgenCallbacks =
            new Dictionary<string, Bindings.ValueChangedCallback>();

        private static void WatchNewgenLook(VisualElement header, long id)
        {
            WatchNumber(header, id, "NewgenAppearanceDescription.Ethnicity", NewgenEthnicityById);
            WatchNumber(header, id, "NewgenAppearanceDescription.SkinTone", NewgenSkinToneById);
        }

        private static void WatchNumber(VisualElement header, long id, string path, Dictionary<long, int> found)
        {
            string watch = id + " " + path;
            if (found.ContainsKey(id) || NewgenCallbacks.ContainsKey(watch) || !PersonNodeKey(header, out var bindings, out var personKey))
                return;
            Bindings.ValueChangedCallback callback = null;
            callback = new Action<Bindings.Key, SI.Core.TypedValue>((key, value) =>
            {
                if (value == null || value.IsNull || !int.TryParse(value.AsString(), out int number))
                    return;
                found[id] = number;
                if (NewgenCallbacks.Remove(watch) && GameSubsystems.TryGet<Bindings>(out var current) && current != null)
                {
                    var unbindKey = key;
                    try { current.Unbind(ref unbindKey, callback); }
                    catch (Exception) { }
                }
            });
            NewgenCallbacks[watch] = callback;
            BindingPaths.Bind(bindings, personKey, path, callback);
        }

        // Fills in the profile's ethnicity from a match, the user's earlier
        // choice or FM's newgen appearance, which is saved while the newgen face
        // shows. newgen says the person is one of FM's newgens: their 3D face
        // shows, or RegenerateFM saved their newgen look before.
        private bool FindKnownEthnicity(long personId, VisualElement picture, ProfileSnapshot profile, out bool newgen)
        {
            string id = personId.ToString(CultureInfo.InvariantCulture);
            newgen = picture != null && IsNewgenPortrait(picture);
            RegenerateFM.KnownEthnicity look = null;
            if (newgen && NewgenEthnicityById.TryGetValue(personId, out int ethnicity) &&
                ethnicity >= 0 && ethnicity < Prompts.Ethnicities.Length)
            {
                look = new RegenerateFM.KnownEthnicity { Ethnicity = Prompts.Ethnicities[ethnicity], Source = "newgen" };
                if (NewgenSkinToneById.TryGetValue(personId, out int tone) && tone >= 1 && tone <= Prompts.SkinTones.Length)
                    look.SkinTone = Prompts.SkinTones[tone - 1];
                SaveEthnicities(new Dictionary<string, RegenerateFM.KnownEthnicity> { [id] = look });
            }
            RegenerateFM.KnownEthnicity known = null;
            try
            {
                known = KnownEthnicities.Load(GraphicsFolder(), id);
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not read saved ethnicities: " + error.Message);
            }
            known = known ?? look;
            if (known == null)
                return false;
            newgen |= known.Source == "newgen";
            profile.Ethnicity = known.Ethnicity;
            profile.SkinTone = known.SkinTone;
            profile.HairColour = known.HairColour;
            return true;
        }

        // Prompts.EthnicityLabels, in the same order, as the ethnicity list shows them.
        private static string[] EthnicityDisplayLabels() => new[]
        {
            Text.T("white Northern European"), Text.T("Mediterranean or Hispanic"), Text.T("North African or Middle Eastern"),
            Text.T("Black African or Caribbean"), Text.T("South Asian"), Text.T("South-East Asian"), Text.T("Pacific Islander"),
            Text.T("Indigenous American"), Text.T("Indigenous Australian"), Text.T("mixed race (Black and white)"), Text.T("East Asian"),
        };

        // Before a face for someone FM hasn't drawn in a match: the AI will guess
        // their ethnicity, unless the user picks one from the list.
        private void AskEthnicity(long personId, bool staff, string name)
        {
            string chosen = null;
            ShowDialog(Text.T("{0} hasn't played in a match you've watched, so we don't know their ethnicity and the AI will guess it from their name and nationality. Generate a face?", name),
                       Text.T("No"), () => CancelSquadScan());
            var dialog = _squadDialog;
            var list = new VisualElement();
            list.style.display = DisplayStyle.None;
            var choice = new Button((Il2CppSystem.Action)(System.Action)(() =>
                list.style.display = list.style.display.value == DisplayStyle.None ? DisplayStyle.Flex : DisplayStyle.None))
            { text = Text.T("Ethnicity: {0} ▾", Text.T("Guess")), focusable = false };
            choice.style.marginTop = 12;
            choice.style.height = 30;
            choice.style.color = Color.white;
            choice.style.backgroundColor = new Color(0.24f, 0.20f, 0.32f, 1f);
            var optionColour = new Color(0.18f, 0.15f, 0.25f, 1f);
            var pickedColour = new Color(0.30f, 0.18f, 0.45f, 1f);
            var optionButtons = new List<Button>();
            var options = new List<(string Label, string Ethnicity)> { (Text.T("Guess"), null) };
            var labels = EthnicityDisplayLabels();
            for (int i = 0; i < Prompts.Ethnicities.Length; i++)
            {
                string label = i < labels.Length ? labels[i] : Prompts.EthnicityLabels[i];
                options.Add((char.ToUpperInvariant(label[0]) + label.Substring(1), Prompts.Ethnicities[i]));
            }
            foreach (var (label, ethnicity) in options)
            {
                Button option = null;
                option = new Button((Il2CppSystem.Action)(System.Action)(() =>
                {
                    chosen = ethnicity;
                    choice.text = Text.T("Ethnicity: {0} ▾", label);
                    foreach (var other in optionButtons)
                        other.style.backgroundColor = other == option ? pickedColour : optionColour;
                    list.style.display = DisplayStyle.None;
                })) { text = label, focusable = false };
                option.style.height = 26;
                option.style.marginTop = 2;
                option.style.unityTextAlign = TextAnchor.MiddleLeft;
                option.style.color = Color.white;
                option.style.backgroundColor = ethnicity == null ? pickedColour : optionColour;
                optionButtons.Add(option);
                list.Add(option);
            }
            var yes = new Button((Il2CppSystem.Action)(System.Action)(() =>
            {
                CancelSquadScan();
                Generate(personId, staff, false, true, chosen);
            })) { text = Text.T("Yes"), focusable = false };
            yes.style.height = 34;
            yes.style.marginTop = 10;
            yes.style.color = Color.white;
            yes.style.backgroundColor = new Color(0.38f, 0.10f, 0.60f, 1f);
            dialog.Insert(1, choice);
            dialog.Insert(2, list);
            dialog.Insert(3, yes);
            Directed(dialog);
            Log.LogInfo("Asked about the ethnicity of " + name + " (" + personId + ")");
        }
    }
}
