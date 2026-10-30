using System;
using System.Collections.Generic;
using System.Globalization;
using SI.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace RegenerateFM.FM26
{
    // Previous face and Remove face (FaceHistory), and each save's generated
    // people kept to their own faces: FM gives them the same IDs in every
    // save, so a face whose owner isn't the person shown with its ID was made
    // in another save, and FaceHistory.Claim swaps it for theirs.
    public sealed partial class Plugin
    {
        // The name each ID was last checked with, so a person is checked once
        // a session, and again when another save shows someone else with it.
        private readonly Dictionary<long, string> _ownerChecked = new Dictionary<long, string>();
        private float _nextListOwnerCheck;

        // The person's name as the profile header shows it, read as
        // CaptureProfile and CaptureStaff read it.
        private static string ShownName(VisualElement header)
        {
            if (header == null)
                return null;
            if (Fm27)
            {
                var texts = TextValues(FindNamed(header, "report-player-banner-max") ?? header);
                return texts.Count > 0 ? texts[0] : null;
            }
            foreach (var value in TextValues(FindNamed(header, "Frame 1000003381")))
            {
                string name = IdPattern.Replace(value, "").Trim(' ', '(', ')');
                if (name.Length > 0)
                    return name;
            }
            return null;
        }

        private static string IdText(long id) => id.ToString(CultureInfo.InvariantCulture);

        // Previous face or Remove face, chosen from a menu for the open profile.
        // ``expected`` is the ID the header showed when the menu opened.
        private void ChangeFace(long? expected, bool remove)
        {
            CheckRefreshTimeout();
            PollGeneration();
            if (_pending != null || _squadBatch != null)
            {
                ShowBusyNotice(Text.T("Face generation is already in progress"));
                return;
            }
            if (WaitingForGraphics)
            {
                ShowBusyNotice(Text.T("Waiting for the previous face to load"));
                return;
            }
            var header = FindNamed(CurrentRoot(), "ReportHeader");
            if (header == null || !HeaderId(header, out long personId) || expected.HasValue && expected.Value != personId)
            {
                Log.LogWarning("The profile changed after the menu opened; its face was left as it was");
                return;
            }
            string name = ShownName(header);
            try
            {
                string folder = GraphicsFolder();
                bool changed = remove ? FaceHistory.Remove(folder, IdText(personId))
                                      : FaceHistory.Previous(folder, IdText(personId), name) != null;
                if (!changed)
                {
                    ShowBusyNotice(remove ? Text.T("No RegenerateFM face to remove")
                                          : Text.T("No earlier face to go back to"));
                    return;
                }
                _ownerChecked[personId] = name;
                Log.LogInfo((remove ? "Removed the face of " : "Brought back an earlier face of ") + name + " (" + personId + ")");
                var picture = FindNamed(header, "PersonPicture");
                var overlay = picture == null ? null : BuildOverlay(picture, out _refreshMessage);
                if (_refreshMessage != null)
                    _refreshMessage.text = Text.T("Loading face…");
                _refreshOverlay = overlay;
                if (!Refresh(purge: true))
                {
                    ShowError(overlay, _refreshMessage, Text.T("Portrait saved, but game refresh failed. Restart FM26 to load it."));
                    _refreshOverlay = null;
                    _refreshMessage = null;
                }
            }
            catch (Exception error)
            {
                Log.LogError("Could not change the face: " + error);
                ShowBusyNotice(error.Message);
            }
        }

        // Whether Previous face and Remove face have anything to do for this person.
        private static (bool Previous, bool Remove) FaceChoices(long personId, string name)
        {
            try
            {
                string folder = GraphicsFolder();
                return (FaceHistory.EarlierCount(folder, IdText(personId), name) > 0,
                        Portraits.CurrentFile(folder, IdText(personId)) != null);
            }
            catch (Exception error)
            {
                Instance?.Log.LogWarning("Could not read the face history: " + error.Message);
                return (false, false);
            }
        }

        // The person on the open profile, and every few seconds those in the
        // shown squad or staff list, against the owners of their faces.
        private void CheckFaceOwners()
        {
            if (_pending != null || _squadBatch != null || _squadScan != null || WaitingForGraphics)
                return;
            var root = CurrentRoot();
            if (root == null)
                return;
            bool changed = false;
            var header = FindNamed(root, "ReportHeader");
            if (header != null && header.worldBound.width >= 800 && HeaderId(header, out long id))
                changed |= ClaimFace(id, ShownName(header));
            if (Time.unscaledTime >= _nextListOwnerCheck)
            {
                _nextListOwnerCheck = Time.unscaledTime + 2f;
                foreach (var kind in new[] { ListKind.Players, ListKind.Staff })
                {
                    var view = FindNamed(ListScroll(kind, root), "View");
                    for (int i = 0; view != null && i < view.hierarchy.childCount; i++)
                    {
                        var row = view.hierarchy.ElementAt(i);
                        var name = kind == ListKind.Staff ? FirstText(row) : FirstText(RowName(row));
                        var address = FindNamed(row, "PersonImage")?.TryCast<SI.UI.Image>()?.m_imageAddressableID?.ToString();
                        if (RowPersonId(address, out long rowId))
                            changed |= ClaimFace(rowId, name);
                    }
                }
            }
            if (changed && !Refresh(purge: true))
                Log.LogWarning("Faces from another save were swapped, but the game's graphics didn't reload");
        }

        // True if the person's face changed.
        private bool ClaimFace(long id, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || _ownerChecked.TryGetValue(id, out string checkedName) && FaceHistory.SameOwner(checkedName, name))
                return false;
            _ownerChecked[id] = name;
            try
            {
                if (!FaceHistory.Claim(GraphicsFolder(), IdText(id), name))
                    return false;
                Log.LogInfo("ID " + id + " is " + name + " in this save: another save's face for the ID was swapped for their own (or FM's)");
                return true;
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not check whose face " + id + " has: " + error.Message);
                return false;
            }
        }

        // A list row's person ID from its picture: "person_<id>" for a photo or
        // a RegenerateFM face, "newgen_<id>" for FM's 3D face. A silhouette has none.
        private static bool RowPersonId(string address, out long id)
        {
            id = 0;
            foreach (var prefix in new[] { "person_", "newgen_" })
                if (address != null && address.StartsWith(prefix, StringComparison.Ordinal))
                    return long.TryParse(address.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id);
            return false;
        }
    }
}
