using System;
using RegenerateFM;
using UnityEngine.UIElements;

namespace RegenerateFM.FM26
{
    // Once a season (an in-game year), the first time the manager's Squad >
    // Overview is shown: an Update aged faces… scan of its players, which says
    // something only if a face was made two or more years ago (AgedFaces). It
    // asks before anything is requested, like the Squad menu's command, and
    // [Faces] OfferAgedFaces = false in settings.ini turns it off.
    public sealed partial class Plugin
    {
        private static readonly TimeSpan AgedCheckInterval = TimeSpan.FromSeconds(5);
        private System.DateTime _agedNextCheckUtc;
        // The year already looked at this session, so the date is read once a year.
        private int _agedCheckedYear;
        // The top bar's date, once found.
        private TextElement _gameDate;

        private void CheckAgedFacesOffer()
        {
            var now = System.DateTime.UtcNow;
            if (now < _agedNextCheckUtc)
                return;
            _agedNextCheckUtc = now + AgedCheckInterval;
            if (_pending != null || WaitingForGraphics || _squadScan != null || _squadBatch != null ||
                (_squadDialog != null && _squadDialog.parent != null) || (_keyDialog != null && _keyDialog.parent != null))
                return;
            var root = CurrentRoot();
            var list = root == null ? null : ListScroll(ListKind.Players, root);
            // Squad > Overview: the manager's own club.
            if (list == null || !InSquadScreen(list))
                return;
            int? year = GameYear(root);
            if (year == null || year == _agedCheckedYear)
                return;
            _agedCheckedYear = year.Value;
            Settings settings;
            string key;
            try
            {
                settings = Settings.Load(Settings.DefaultPath);
                key = KeyStore.Load(KeyStore.DefaultPath);
            }
            catch (Exception error)
            {
                Log.LogWarning("Aged faces offer skipped: " + error.Message);
                return;
            }
            // Nothing to pay with yet, or turned off: no offer.
            if (!settings.OfferAgedFaces || string.IsNullOrWhiteSpace(key) || AgedFaces.Offered(AgedFaces.OfferedPath, Text.GameName, year.Value))
                return;
            AgedFaces.Record(AgedFaces.OfferedPath, Text.GameName, year.Value);
            Log.LogInfo("RegenerateFM looks for aged faces once in " + year.Value);
            StartSquadScan(ListKind.Players, ageing: true, quiet: true);
        }

        // The year of the game's date in its top bar, or null.
        private int? GameYear(VisualElement root)
        {
            if (_gameDate != null && _gameDate.panel != null && AgedFaces.GameYear(_gameDate.text) is int known)
                return known;
            _gameDate = FindGameDate(root);
            return _gameDate == null ? null : AgedFaces.GameYear(_gameDate.text);
        }

        private static TextElement FindGameDate(VisualElement element)
        {
            if (element == null || element.worldBound.width <= 0 || element.worldBound.height <= 0)
                return null;
            var text = element.TryCast<TextElement>();
            // The date sits in the top bar, above the screen's own content.
            if (text != null && element.worldBound.y < 60 && AgedFaces.GameYear(text.text) != null)
                return text;
            for (int i = 0; i < element.hierarchy.childCount; i++)
                if (FindGameDate(element.hierarchy.ElementAt(i)) is TextElement found)
                    return found;
            return null;
        }
    }
}
