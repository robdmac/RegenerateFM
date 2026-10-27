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
    // Reading the open profile from FM26's screen and bindings: attributes, gender,
    // family, biography.
    public sealed partial class Plugin
    {
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
                FirstText(FindNamed(header, "Frame 1000003767")) is string contract && Prompts.NoClub(contract))
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
                WatchNewgenLook(header, id);
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
                var family = Prompts.FamilyRelatives(profile);
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
                try { plugin.ReadMatchEthnicities(); }
                catch (Exception error) { plugin.Log.LogWarning("Match appearance scan failed: " + error.Message); }
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
                try { plugin.CheckSquadMenu(); plugin.CheckStaffMenu(); plugin.PollSquadScan(); }
                catch (Exception error) { plugin.Log.LogWarning("Squad scan failed: " + error); plugin.CancelSquadScan(); }
                try { plugin.PollSquadBatch(); }
                catch (Exception error) { plugin.StopSquadBatch("Squad generation stopped: " + error.Message); }
            }
        }
    }
}
