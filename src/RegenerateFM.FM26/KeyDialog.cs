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
    // The key dialog: an OpenAI key or a RegenerateFM credits sign-in, and RegenerateFM's
    // own dialogs.
    public sealed partial class Plugin
    {
        // True when a key is saved. Otherwise opens the key dialog, which runs
        // `next` once a key has been checked and saved.
        private bool KeyReady(Action next)
        {
            try
            {
                if (KeyStore.Load(KeyStore.DefaultPath) != null)
                    return true;
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not read the saved OpenAI key: " + error.Message);
                ShowKeyDialog("RegenerateFM couldn't read the saved key. Paste it again.", next);
                return false;
            }
            ShowKeyDialog(null, next);
            return false;
        }

        private void ShowKeyDialog(string problem, Action next, bool signedIn = false)
        {
            CloseKeyDialog();
            _keyDialogSignedIn = signedIn;
            var root = CurrentRoot();
            if (root == null)
                return;
            var dialog = new VisualElement { name = "regeneratefm-key-dialog" };
            dialog.style.position = Position.Absolute;
            dialog.style.right = 24;
            dialog.style.top = 80;
            dialog.style.width = 420;
            dialog.style.paddingTop = 16;
            dialog.style.paddingBottom = 16;
            dialog.style.paddingLeft = 16;
            dialog.style.paddingRight = 16;
            dialog.style.backgroundColor = new Color(0.13f, 0.10f, 0.18f, 0.97f);
            Func<string, int, Label> text = (value, size) =>
            {
                var label = new Label(value) { pickingMode = PickingMode.Ignore };
                label.style.color = Color.white;
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.fontSize = size;
                label.style.marginBottom = 8;
                dialog.Add(label);
                return label;
            };
            text("RegenerateFM: paying for faces", 18);
            if (problem != null)
                text(problem, 14).style.color = new Color(1f, 0.82f, 0.45f, 1f);
            text(Credits.BuyText, 14);
            var buy = new Button((Il2CppSystem.Action)(System.Action)(() => BuyCredits()))
            { text = "Buy credits / Sign in", focusable = false };
            buy.style.height = 34;
            buy.style.marginBottom = 14;
            buy.style.color = Color.white;
            buy.style.backgroundColor = new Color(0.38f, 0.10f, 0.60f, 1f);
            dialog.Add(buy);
            text("Or use your own OpenAI API key. Each face is then one billed request on your OpenAI account, " +
                 "usually a few cents or less.", 13);
            var open = new Button((Il2CppSystem.Action)(System.Action)(() => Application.OpenURL(KeyStore.KeysPage)))
            { text = "Get a key at platform.openai.com/api-keys", focusable = false };
            open.style.height = 30;
            open.style.marginBottom = 10;
            dialog.Add(open);
            var field = new TextField { isPasswordField = true, maskChar = '•' };
            // FM26's text field doesn't paste on macOS (the game has no Edit menu
            // for Cmd+V), so there is a Paste button, and Cmd+V or Ctrl+V reaching
            // the field is handled here too: the clipboard replaces its text.
            Action paste = () =>
            {
                var clip = (GUIUtility.systemCopyBuffer ?? "").Trim();
                if (clip.Length > 0)
                    field.value = clip;
                if (_keyStatus != null)
                    _keyStatus.text = clip.Length > 0
                        ? "Pasted " + clip.Length + " characters. Choose Save key."
                        : "The clipboard is empty. Copy your key, then choose Paste.";
            };
            field.RegisterCallback(DelegateSupport.ConvertDelegate<EventCallback<KeyDownEvent>>(
                new Action<KeyDownEvent>(key =>
                {
                    if (key.keyCode != KeyCode.V || !(key.commandKey || key.ctrlKey))
                        return;
                    paste();
                    key.StopPropagation();
                })), TrickleDown.TrickleDown);
            // The dialog is 420 wide with 16 padding each side: 388 for both.
            field.style.height = 30;
            field.style.width = 312;
            field.style.flexShrink = 0;
            var pasteButton = new Button((Il2CppSystem.Action)(System.Action)(() => paste()))
            { text = "Paste", focusable = false };
            pasteButton.style.height = 30;
            pasteButton.style.width = 70;
            pasteButton.style.marginLeft = 6;
            pasteButton.style.flexShrink = 0;
            var entry = new VisualElement();
            entry.style.flexDirection = FlexDirection.Row;
            entry.Add(field);
            entry.Add(pasteButton);
            dialog.Add(entry);
            _keyStatus = text("Paste or type the key (it starts with sk-), then choose Save key.", 13);
            _keyStatus.style.marginTop = 6;
            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.justifyContent = Justify.FlexEnd;
            var cancel = new Button((Il2CppSystem.Action)(System.Action)(() => CloseKeyDialog())) { text = "Cancel", focusable = false };
            var save = new Button((Il2CppSystem.Action)(System.Action)(() => SaveKey(field.value)))
            { text = "Save key", focusable = false };
            save.style.color = Color.white;
            save.style.backgroundColor = new Color(0.38f, 0.10f, 0.60f, 1f);
            foreach (var button in new[] { cancel, save })
            {
                button.style.height = 32;
                button.style.marginLeft = 8;
                buttons.Add(button);
            }
            dialog.Add(buttons);
            text("The key is kept on this computer only, " +
                 (OperatingSystem.IsWindows() ? "encrypted for your Windows user" : "readable only by your user account") + ", in " +
                 Path.GetDirectoryName(KeyStore.DefaultPath) + ". Delete that folder to forget it.", 12);
            var questions = new Button((Il2CppSystem.Action)(System.Action)(() => ShowKeyFaq(dialog)))
            { text = "Questions: why it costs money, and which option is cheaper", focusable = false };
            questions.style.height = 26;
            questions.style.marginTop = 4;
            questions.style.fontSize = 12;
            questions.style.color = new Color(0.78f, 0.70f, 1f, 1f);
            questions.style.backgroundColor = new Color(0, 0, 0, 0);
            questions.style.borderTopWidth = 0;
            questions.style.borderBottomWidth = 0;
            questions.style.borderLeftWidth = 0;
            questions.style.borderRightWidth = 0;
            dialog.Add(questions);
            root.Add(dialog);
            _keyDialog = dialog;
            _keyField = field;
            _afterKeySaved = next;
            field.Focus();
        }

        private void SaveKey(string value)
        {
            if (_keyCheck != null)
                return;
            string key;
            try
            {
                key = KeyStore.Clean(value);
            }
            catch (ArgumentException error)
            {
                _keyStatus.text = error.Message;
                return;
            }
            ImageSettings settings;
            try
            {
                settings = Settings.Load(Settings.DefaultPath).ForKey(key);
            }
            catch (Exception error)
            {
                _keyStatus.text = error.Message;
                return;
            }
            _keyStatus.text = Credits.IsToken(key) ? "Checking the RegenerateFM sign-in…" : "Checking the key with OpenAI (this is free)…";
            _keyChecking = key;
            _keyCheck = ImageApi.CheckKey(settings, key);
        }

        private void PollKeyCheck()
        {
            var check = _keyCheck;
            if (check == null || !check.IsCompleted)
                return;
            _keyCheck = null;
            string key = _keyChecking;
            _keyChecking = null;
            string problem;
            try
            {
                problem = check.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                problem = "Could not check the key: " + error.Message;
            }
            if (_keyDialog == null || _keyDialog.parent == null)
                return;
            if (problem != null)
            {
                _keyStatus.text = problem;
                return;
            }
            try
            {
                KeyStore.Save(KeyStore.DefaultPath, key);
            }
            catch (Exception error)
            {
                _keyStatus.text = "Could not save the key: " + error.Message;
                Log.LogError("Could not save the OpenAI key: " + error);
                return;
            }
            Log.LogInfo("OpenAI key checked and saved to " + KeyStore.DefaultPath);
            var next = _afterKeySaved;
            CloseKeyDialog();
            try
            {
                next?.Invoke();
            }
            catch (Exception error)
            {
                Log.LogError("Could not continue after saving the key: " + error);
            }
        }

        // The key dialog's questions (the same as the credits pages' FAQ, in
        // regeneratefm-server/src/pages.ts), shown in place of the dialog's
        // controls until Back. A question's link opens in the browser.
        private static readonly (string Question, string Answer, string LinkText, string Link)[] KeyFaq =
        {
            ("Why does making a face cost money?",
             "Each face is drawn by OpenAI's image model, and OpenAI charges for every image it makes. RegenerateFM doesn't run " +
             "its own AI, so every face is one paid request to OpenAI, whether you use credits or your own key.", null, null),
            ("Which is cheaper, credits or my own OpenAI API key?",
             "Your own key. You then pay OpenAI's price for each image directly, which at RegenerateFM's default quality is " +
             "about 1.2p (1.6 US cents) a face. Credits cost 2p to 2.7p each (2.4 to 3.3 cents in euros or dollars), " +
             "but you don't need an OpenAI account, billing set up with OpenAI, or a key to copy into the game.",
             "OpenAI's API pricing", "https://openai.com/api/pricing/"),
            ("Why do credits cost more than OpenAI's own price?",
             "Credits pay for more than the image: VAT or sales tax, the card payment (each payment has a fixed fee, which " +
             "is why small packs cost more per credit), Stripe handling tax, fraud and refunds for every sale, the service " +
             "that signs you in and passes each request on to OpenAI, and the credit we give back when an image fails.",
             null, null),
            ("Can I use my ChatGPT Plus or Pro subscription instead?",
             "Not yet. A ChatGPT subscription and OpenAI's API are separate products. OpenAI has started letting some apps " +
             "use a ChatGPT plan through \u201cSign in with ChatGPT\u201d, but it's a preview, and image generation is one " +
             "of the things it doesn't support yet. If OpenAI adds it, RegenerateFM will offer it.",
             "OpenAI: Sign in with ChatGPT limitations",
             "https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations"),
        };

        private void ShowKeyFaq(VisualElement dialog)
        {
            // Hide the dialog's controls (the typed key stays in its field) and show the answers.
            var hidden = new List<VisualElement>();
            for (int i = 0; i < dialog.childCount; i++)
            {
                var child = dialog[i];
                if (child.resolvedStyle.display != DisplayStyle.None)
                {
                    hidden.Add(child);
                    child.style.display = DisplayStyle.None;
                }
            }
            var faq = new ScrollView { name = "regeneratefm-key-faq" };
            faq.style.maxHeight = 460;
            Func<string, int, bool, Label> add = (value, size, bold) =>
            {
                var label = new Label(value) { pickingMode = PickingMode.Ignore };
                label.style.color = bold ? Color.white : new Color(0.82f, 0.80f, 0.88f, 1f);
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.fontSize = size;
                label.style.marginBottom = bold ? 4 : 6;
                if (bold)
                    label.style.unityFontStyleAndWeight = FontStyle.Bold;
                faq.Add(label);
                return label;
            };
            add("Questions", 18, true);
            foreach (var item in KeyFaq)
            {
                add(item.Question, 14, true).style.marginTop = 8;
                add(item.Answer, 13, false);
                if (item.Link != null)
                {
                    string url = item.Link;
                    var link = new Button((Il2CppSystem.Action)(System.Action)(() => Application.OpenURL(url)))
                    { text = item.LinkText + " \u2197", focusable = false };
                    link.style.alignSelf = Align.FlexStart;
                    link.style.height = 24;
                    link.style.fontSize = 12;
                    faq.Add(link);
                }
            }
            dialog.Add(faq);
            var back = new Button((Il2CppSystem.Action)(System.Action)(() =>
            {
                faq.RemoveFromHierarchy();
                foreach (var child in hidden)
                    child.style.display = StyleKeyword.Null;
            }))
            { text = "Back", focusable = false };
            back.style.height = 32;
            back.style.marginTop = 10;
            faq.Add(back);
        }

        // A failed face whose key needs attention: sign in again, or buy more credits.
        private void ShowKeyProblem(GenerationResult result)
        {
            string key = null;
            try { key = KeyStore.Load(KeyStore.DefaultPath); } catch (Exception) { }
            if (result.OutOfCredits)
                ShowKeyDialog("You've used all your RegenerateFM credits. Buy more to carry on.", null, signedIn: Credits.IsToken(key));
            else if (result.KeyRejected)
                ShowKeyDialog(Credits.IsToken(key)
                    ? "Your RegenerateFM sign-in isn't valid any more. Sign in again."
                    : "OpenAI didn't accept the saved key. Paste a new one.", null);
        }

        // Buy credits / Sign in: a signed-in game opens the credits page in the
        // browser; otherwise a device sign-in starts, and PollCreditsSignIn
        // saves the token once the player has finished in the browser.
        private void BuyCredits()
        {
            if (_creditsStart != null || _creditsSignIn != null)
                return;
            if (_keyDialogSignedIn)
            {
                Application.OpenURL(CreditsServiceUrl() + "/link");
                _keyStatus.text = "The credits page is open in your browser. Your new credits work as soon as you've paid.";
                return;
            }
            string service;
            try
            {
                service = CreditsServiceUrl();
            }
            catch (ArgumentException error)
            {
                _keyStatus.text = error.Message;
                return;
            }
            _keyStatus.text = "Opening your browser…";
            _creditsStart = Credits.Start(service);
        }

        private void PollCreditsSignIn()
        {
            if (_creditsStart != null && _creditsStart.IsCompleted)
            {
                var start = _creditsStart;
                _creditsStart = null;
                try
                {
                    _creditsSignIn = start.GetAwaiter().GetResult();
                }
                catch (Exception error)
                {
                    Log.LogWarning("RegenerateFM credits sign-in didn't start: " + error.Message);
                    if (_keyStatus != null)
                        _keyStatus.text = "RegenerateFM credits can't be reached right now. Try again in a moment.";
                    return;
                }
                Application.OpenURL(_creditsSignIn.Link);
                _creditsNextPollUtc = System.DateTime.UtcNow.AddSeconds(_creditsSignIn.Interval);
                if (_keyStatus != null)
                    _keyStatus.text = "In your browser, sign in and confirm the code " + _creditsSignIn.UserCode +
                                      ". RegenerateFM is waiting…";
            }
            var signIn = _creditsSignIn;
            if (signIn == null)
                return;
            if (_keyDialog == null || _keyDialog.parent == null || System.DateTime.UtcNow > signIn.ExpiresUtc)
            {
                // Closed, or the code expired: stop waiting.
                if (_keyStatus != null && System.DateTime.UtcNow > signIn.ExpiresUtc)
                    _keyStatus.text = "The sign-in expired. Choose Buy credits / Sign in again.";
                _creditsSignIn = null;
                _creditsPoll = null;
                return;
            }
            if (_creditsPoll == null)
            {
                if (System.DateTime.UtcNow >= _creditsNextPollUtc)
                    _creditsPoll = Credits.Check(CreditsServiceUrl(), signIn.DeviceCode);
                return;
            }
            if (!_creditsPoll.IsCompleted)
                return;
            Credits.Poll poll;
            try
            {
                poll = _creditsPoll.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                Log.LogWarning("RegenerateFM credits sign-in check failed: " + error.Message);
                poll = new Credits.Poll { Status = "pending" };
            }
            _creditsPoll = null;
            _creditsNextPollUtc = System.DateTime.UtcNow.AddSeconds(signIn.Interval * (poll.Status == "slow_down" ? 2 : 1));
            if (poll.Status == "expired")
            {
                _creditsSignIn = null;
                _keyStatus.text = "The sign-in expired. Choose Buy credits / Sign in again.";
                return;
            }
            if (poll.Status != "approved")
                return;
            _creditsSignIn = null;
            try
            {
                KeyStore.Save(KeyStore.DefaultPath, poll.Token);
            }
            catch (Exception error)
            {
                _keyStatus.text = "Could not save the sign-in: " + error.Message;
                Log.LogError("Could not save the RegenerateFM credits token: " + error);
                return;
            }
            Log.LogInfo("Signed in to RegenerateFM credits (" + poll.Credits + " credits); token saved to " + KeyStore.DefaultPath);
            if (poll.Credits == 0)
            {
                // Signed in but nothing bought yet: keep the dialog so the player can buy.
                _keyStatus.text = "Signed in as " + poll.Email + ". You have no credits yet: choose a pack in your browser, " +
                                  "then choose the face again.";
                _keyDialogSignedIn = true;
                return;
            }
            var next = _afterKeySaved;
            CloseKeyDialog();
            try
            {
                next?.Invoke();
            }
            catch (Exception error)
            {
                Log.LogError("Could not continue after signing in: " + error);
            }
        }

        private void CloseKeyDialog()
        {
            if (_keyDialog != null && _keyDialog.parent != null)
                _keyDialog.RemoveFromHierarchy();
            _keyDialog = null;
            _keyField = null;
            _keyStatus = null;
            _afterKeySaved = null;
            PollKeyTyping();
        }

        // FM26 reads its keyboard shortcuts directly, so letters typed into the
        // key field would also drive the game. Its own text boxes switch on
        // "exclusive input" while in use; do the same while the dialog is open.
        private void PollKeyTyping()
        {
            bool open = _keyField != null && _keyField.panel != null;
            if (open == _keyTyping)
                return;
            try
            {
                var input = ManualSingleton<InputManager>.Instance;
                if (input == null)
                    return;
                if (open)
                    input.OnExclusiveInputBegin();
                else
                    input.OnExclusiveInputEnd();
                _keyTyping = open;
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not switch FM26's keyboard shortcuts for the key field: " + error.Message);
                _keyTyping = open;
            }
        }

        private Label ShowDialog(string text, string buttonText, Action buttonAction)
        {
            CancelSquadScan();
            var root = CurrentRoot();
            var dialog = new VisualElement { name = "regeneratefm-squad-dialog" };
            dialog.style.position = Position.Absolute;
            // Centred over the page.
            dialog.style.left = Math.Max(0f, (root.layout.width - 360f) / 2f);
            dialog.style.top = Math.Max(0f, root.layout.height / 3f - 80f);
            dialog.style.width = 360;
            dialog.style.paddingTop = 16;
            dialog.style.paddingBottom = 16;
            dialog.style.paddingLeft = 16;
            dialog.style.paddingRight = 16;
            dialog.style.backgroundColor = new Color(0.13f, 0.10f, 0.18f, 0.97f);
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.style.color = Color.white;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 16;
            dialog.Add(label);
            var button = new Button((Il2CppSystem.Action)(System.Action)(buttonAction))
            { text = buttonText, focusable = false };
            button.style.marginTop = 12;
            button.style.height = 32;
            dialog.Add(button);
            root.Add(dialog);
            _squadDialog = dialog;
            _dialogPage = DialogPage(root);
            _dialogPageMisses = 0;
            WatchDialogEscape(root);
            return label;
        }

        // The page a dialog was shown over: the profile's person, or the squad
        // list's team. A message closes when you leave that page or press Escape;
        // a running squad scan or batch keeps its dialog until it ends.
        private string _dialogPage;
        private int _dialogPageMisses;
        private VisualElement _escapeRoot;

        private static string DialogPage(VisualElement root)
        {
            var header = FindNamed(root, "ReportHeader");
            if (header != null && HeaderId(header, out long id))
                return "person " + id;
            return SquadTable(root) != null ? "squad " + (SquadPageTeam(root) ?? "") : null;
        }

        private void CheckDialogPage()
        {
            if (_squadDialog == null || _squadScan != null || _squadBatch != null)
                return;
            if (_squadDialog.parent == null)
            {
                _squadDialog = null;
                return;
            }
            // Twice in a row (a second apart), so a page redrawing doesn't close it.
            if (_dialogPage == null || DialogPage(CurrentRoot()) == _dialogPage)
                _dialogPageMisses = 0;
            else if (++_dialogPageMisses >= 2)
                CancelSquadScan();
        }

        private void WatchDialogEscape(VisualElement root)
        {
            var top = root?.panel?.visualTree;
            if (top == null || top == _escapeRoot)
                return;
            _escapeRoot = top;
            top.RegisterCallback(DelegateSupport.ConvertDelegate<EventCallback<KeyDownEvent>>(
                new Action<KeyDownEvent>(e =>
                {
                    if (e.keyCode != KeyCode.Escape || _squadDialog == null || _squadDialog.parent == null ||
                        _squadBatch != null)
                        return;
                    e.StopPropagation();
                    CancelSquadScan();
                })), TrickleDown.TrickleDown);
        }
    }
}
