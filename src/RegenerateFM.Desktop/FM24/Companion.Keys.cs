using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RegenerateFM.Desktop.FM24.Bridge;

namespace RegenerateFM.Desktop.FM24
{
    // The OpenAI key or RegenerateFM credits sign-in: asked for in FM24 on the
    // Mac (the library's prompt), in RegenerateFM's key window on Windows and
    // Proton, or in the companion's own terminal. Either is kept in KeyStore's
    // file, which the FM26 plugin also reads.
    public sealed partial class Companion
    {
        // What the key prompts return for Buy credits / Sign in (never a valid
        // key): KeyWindow's, and key_prompt_macos.m's CREDITS_CHOICE.
        public const string CreditsChoice = "regeneratefm:credits";
        public const string KeyCost = "Each face is then one billed request on your OpenAI account, usually a few cents or less.";
        public static string KeyWindowNotice => Text.T("Buy credits or paste your OpenAI key in the RegenerateFM: credits or OpenAI key window.");

        // Replaced by tests: the key window and the key check.
        public Func<string, string> AskInWindow = text => KeyWindow.Ask(text, link: KeyStore.KeysPage, credits: true);
        public Func<string, bool> OpenBrowser = Browser.Open;
        public Func<string, string> CheckKey = key =>
            ImageApi.CheckKey(RegenerateFM.Settings.Load(RegenerateFM.Settings.DefaultPath).ForKey(key), key).GetAwaiter().GetResult();

        // FM24 for macOS asks in the game, like FM26.
        private bool AsksInGame => OperatingSystem.IsMacOS() && ReloadPort != 0;

        // Started by FM24 on Windows or Proton: no window of its own, so a key is
        // asked for in RegenerateFM's key window.
        private bool AsksInWindow => OperatingSystem.IsWindows() && StartedByFm24 != null;

        private string CreditsText => Credits.BuyText + " " + Text.T("Choose Buy credits / Sign in.");

        // The key prompt's first line once the saved key or sign-in was refused.
        private string RejectedText() =>
            Credits.IsToken(SavedKey())
                ? Text.T("Your RegenerateFM credits sign-in has ended or its credits have run out. Choose Buy credits / Sign in.")
                : Text.T("OpenAI didn't accept the saved key. Paste a new one.");

        // A message in FM24's own dialog, or the log if FM24 can't show it.
        private void Tell(string text)
        {
            Log(text);
            if (ReloadPort == 0)
                return;
            try
            {
                Dll.ShowDialog(ReloadPort, Token, "message", Text.T("RegenerateFM credits"), Dll.ClipText(text));
            }
            catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException ||
                                          error is System.Net.Sockets.SocketException)
            {
            }
        }

        // Offers the page of a newer release (UpdateCheck), if there is one. FM24
        // can't show a dialog while it's starting, so it tries every ``wait``.
        public void OfferUpdate(string newer, TimeSpan wait)
        {
            if (newer == null)
                return;
            Log("RegenerateFM " + newer + " is available: " + UpdateCheck.ReleasesPage);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Thread.Sleep(wait);
                try
                {
                    string text = Text.T("RegenerateFM {0} is available.", newer) + " " + Text.T("Open the download page?");
                    if (Dll.ShowDialog(ReloadPort, Token, "confirm", "RegenerateFM", Dll.ClipText(text)) == "yes")
                        OpenBrowser(UpdateCheck.ReleasesPage);
                    return;
                }
                catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException ||
                                              error is System.Net.Sockets.SocketException)
                {
                }
            }
        }

        private string CheckAndSave(string entered)
        {
            string key;
            try
            {
                key = KeyStore.Clean(entered);
            }
            catch (ArgumentException error)
            {
                return error.Message;
            }
            string problem;
            try
            {
                problem = CheckKey(key);
            }
            catch (ArgumentException error)
            {
                return error.Message;  // settings.ini names an address that isn't allowed
            }
            if (problem != null)
                return problem;
            try
            {
                KeyStore.Save(KeyStore.DefaultPath, key);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException)
            {
                return Text.T("Could not save the key: {0}", error.Message);
            }
            KeyRejected = false;
            Log("OpenAI key checked and saved in " + KeyStore.Folder + ".");
            return null;
        }

        // Buy credits / Sign in: starts a device sign-in with RegenerateFM
        // credits, opens it in the browser, waits for the player to sign in
        // there, and saves the token where an OpenAI key goes. Null once a token
        // is saved, otherwise the problem to show at the key prompt.
        public string SignInWithCredits(Action<string> tell = null)
        {
            tell ??= Tell;
            string service;
            Credits.SignIn signIn;
            try
            {
                service = RegenerateFM.Settings.Load(RegenerateFM.Settings.DefaultPath).CreditsServiceUrl;
                signIn = Credits.Start(service).GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException ||
                                          error is System.Text.Json.JsonException || error is System.Collections.Generic.KeyNotFoundException)
            {
                return error.Message;
            }
            bool opened = OpenBrowser(signIn.Link);
            tell(opened
                ? Text.T("Your browser is open at RegenerateFM credits. Sign in with your email, buy credits if you need them, and confirm the code {0}. RegenerateFM carries on by itself once you've signed in.",
                         signIn.UserCode)
                : Text.T("Open {0} in your browser. Sign in with your email, buy credits if you need them, and confirm the code {1}. RegenerateFM carries on by itself once you've signed in.",
                         signIn.Link, signIn.UserCode));
            Credits.Poll answer;
            try
            {
                answer = Credits.Wait(service, signIn).GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is InvalidOperationException || error is System.Text.Json.JsonException ||
                                          error is System.Collections.Generic.KeyNotFoundException)
            {
                return error.Message;
            }
            if (answer == null)
                return Text.T("The sign-in expired before it was confirmed. Choose Buy credits / Sign in again.");
            try
            {
                KeyStore.Save(KeyStore.DefaultPath, answer.Token);
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is UnauthorizedAccessException ||
                                          error is InvalidDataException)
            {
                return Text.T("Could not save the sign-in: {0}", error.Message);
            }
            KeyRejected = false;
            tell(answer.Credits == 1 ? Text.T("Signed in to RegenerateFM credits as {0}, with 1 credit.", answer.Email)
                 : answer.Credits > 0 ? Text.T("Signed in to RegenerateFM credits as {0}, with {1} credits.", answer.Email, answer.Credits)
                 : Text.T("Signed in to RegenerateFM credits as {0}, with {1} credits. Buy credits in the browser page, then choose the face command again.",
                          answer.Email, answer.Credits));
            return null;
        }

        // With no key saved (or one OpenAI rejected), asks in RegenerateFM's key
        // window (an ordinary Windows edit box, which takes a paste under Wine
        // too), checks the key with OpenAI (free) and saves it. True once a key
        // is saved.
        public bool AskKeyInWindow(bool tellFm24 = true)
        {
            lock (_keyLock)
            {
                if (SavedKey() != null && !KeyRejected)
                    return true;
                if (tellFm24 && ReloadPort != 0)
                {
                    try
                    {
                        Dll.ShowDialog(ReloadPort, Token, "message", "RegenerateFM", KeyWindowNotice);
                    }
                    catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException ||
                                                  error is System.Net.Sockets.SocketException)
                    {
                    }
                }
                bool unavailable = false;
                bool saved = AskUntilSaved(KeyRejected ? RejectedText() : null,
                    Text.T("Paste it (it starts with sk-) with Ctrl+V or type it, then choose Save key."),
                    Text.T("The key or sign-in is saved encrypted for your Windows user in {0}. Delete that folder to forget it.", KeyStore.Folder),
                    text =>
                    {
                        try
                        {
                            return AskInWindow(text);
                        }
                        catch (Exception error) when (error is IOException || error is InvalidOperationException)
                        {
                            Log("Couldn't show the key dialog (" + error.Message + ").");
                            unavailable = true;
                            return null;
                        }
                    });
                if (!saved && !unavailable)
                    Log("No OpenAI key was saved; choosing a face command asks again.");
                return saved;
            }
        }

        // Asks with ``ask`` (null when the player cancels) until a key or credits
        // sign-in is saved; false if cancelled.
        private bool AskUntilSaved(string problem, string howToPaste, string whereSaved, Func<string, string> ask)
        {
            while (true)
            {
                string text = (problem != null ? problem + "\n\n" : "") + CreditsText + "\n\n" +
                              Text.T("Or use your own OpenAI API key. Each face is then one billed request on your OpenAI account, usually a few cents or less. {0} Get a key… opens {1}.",
                                     howToPaste, KeyStore.KeysPage) +
                              "\n\n" + whereSaved;
                string entered = ask(text);
                if (entered == null)
                    return false;
                problem = entered == CreditsChoice ? SignInWithCredits() : CheckAndSave(entered);
                if (problem == null)
                    return true;
            }
        }

        // FM24 for macOS asks for the key in the game, like FM26: with no key
        // saved (or one OpenAI rejected), shows the library's key prompt, checks
        // the key with OpenAI (free) and saves it. On Windows and Proton the key
        // window asks instead. False when the player cancels; true otherwise (a
        // missing key elsewhere fails later with MissingKey).
        public bool EnsureKey()
        {
            if (!PreviewOnly && AsksInWindow)
                return AskKeyInWindow();
            if (PreviewOnly || !AsksInGame)
                return true;
            string problem = null;
            if (SavedKey() != null)
            {
                if (!KeyRejected)
                    return true;
                problem = RejectedText();
            }
            lock (_keyLock)
            {
                bool unavailable = false;
                bool saved = AskUntilSaved(problem, Text.T("Paste or type it (it starts with sk-), then choose Save key."),
                    Text.T("The key or sign-in is kept on this Mac only, readable only by your user account, in {0}. Delete that folder to forget it.",
                           KeyStore.Folder),
                    text =>
                    {
                        try
                        {
                            return Dll.AskKey(ReloadPort, Token, text);
                        }
                        catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException ||
                                                      error is TimeoutException || error is System.Net.Sockets.SocketException)
                        {
                            Log("FM24 couldn't ask for the OpenAI key (" + error.Message + ").");
                            unavailable = true;
                            return null;
                        }
                    });
                return saved || unavailable;
            }
        }

        // At start: a saved key (or credits sign-in) now refused is asked for again.
        public void CheckSavedKey()
        {
            string saved = SavedKey();
            if (saved == null)
                return;
            string problem;
            try
            {
                problem = CheckKey(saved);
            }
            catch (ArgumentException)
            {
                return;
            }
            if (problem == ImageApi.KeyRejected || problem == ImageApi.SignedOut)
            {
                KeyRejected = true;
                Log("OpenAI no longer accepts the saved key; FM24 will ask for a new one.");
            }
        }

        // With no key saved, asks for one in this terminal. The key is checked
        // with OpenAI (free) and saved in KeyStore's file, which the FM26 plugin
        // also reads.
        public void AskForKeyInTerminal()
        {
            if (Console.IsInputRedirected)
                return;
            string saved = SavedKey();
            if (saved != null)
            {
                // A saved key can be revoked later; checking it is free. Ask again
                // only when OpenAI says no, not when it can't be reached.
                string check;
                try
                {
                    check = CheckKey(saved);
                }
                catch (ArgumentException)
                {
                    return;
                }
                if (check != ImageApi.KeyRejected && check != ImageApi.SignedOut)
                    return;
                Log(RejectedText());
            }
            Log(Credits.BuyText + " Type credits and press Enter to sign in.\n" +
                "Or use your own OpenAI API key. " + KeyCost + " Create one at " + KeyStore.KeysPage + ", then paste it " +
                "here (right-click or Ctrl+V) or type it. It isn't shown as you type. Press Enter on an empty line to skip.");
            while (true)
            {
                string entered = Terminal.ReadSecret("OpenAI API key: ");
                if (entered == null)
                    return;
                if (entered.Trim().Length == 0)
                {
                    Log("No key saved. Faces can't be made until you restart the companion and enter one.");
                    return;
                }
                string problem;
                if (entered.Trim().Equals("credits", StringComparison.OrdinalIgnoreCase))
                {
                    int port = ReloadPort;
                    ReloadPort = 0;  // The terminal tells the player, not FM24.
                    try
                    {
                        problem = SignInWithCredits(Log);
                    }
                    finally
                    {
                        ReloadPort = port;
                    }
                }
                else
                {
                    Log("Checking the key with OpenAI (this is free)...");
                    problem = CheckAndSave(entered);
                }
                if (problem == null)
                {
                    Log("Saved, " + (OperatingSystem.IsWindows() ? "encrypted for your Windows user" : "readable only by you") +
                        ", in " + KeyStore.Folder + ". Delete that folder to forget it.");
                    return;
                }
                Log(problem);
            }
        }
    }

    // Opens a web page in the player's browser.
    public static class Browser
    {
        public static bool Open(string url)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                else
                    Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url);
                return true;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception || error is InvalidOperationException)
            {
                return false;
            }
        }
    }

    // Reading from the companion's own terminal.
    public static class Terminal
    {
        // A line typed without showing it (dots-free, as getpass), or null at the end of input.
        public static string ReadSecret(string prompt)
        {
            Console.Write(prompt);
            bool controlC = false;
            try
            {
                // Ctrl+C at the prompt carries on without a key, as with getpass.
                controlC = Console.TreatControlCAsInput;
                Console.TreatControlCAsInput = true;
                return ReadHidden();
            }
            catch (IOException)
            {
                return Console.ReadLine();
            }
            finally
            {
                try
                {
                    Console.TreatControlCAsInput = controlC;
                }
                catch (IOException)
                {
                }
            }
        }

        private static string ReadHidden()
        {
            var typed = new System.Text.StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key;
                try
                {
                    key = Console.ReadKey(intercept: true);
                }
                catch (InvalidOperationException)
                {
                    return Console.ReadLine();
                }
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return typed.ToString();
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (typed.Length > 0)
                        typed.Length--;
                }
                else if (key.KeyChar == '\u0004' && typed.Length == 0)
                    return null;
                else if (key.KeyChar == '\u0003')
                {
                    Console.WriteLine();
                    return null;
                }
                else if (!char.IsControl(key.KeyChar))
                    typed.Append(key.KeyChar);
            }
        }

        // Keeps a double-clicked window readable until Enter.
        public static void Pause(string prompt = "Press Enter to close.")
        {
            if (Console.IsInputRedirected)
                return;
            Console.Write(prompt);
            try
            {
                Console.ReadLine();
            }
            catch (IOException)
            {
            }
        }
    }
}
