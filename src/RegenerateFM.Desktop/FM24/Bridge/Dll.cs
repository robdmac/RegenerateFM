using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RegenerateFM.Desktop.FM24.Bridge
{
    // The DLL couldn't show the dialog; the caller uses its fallback.
    public sealed class DialogUnavailableException : Exception
    {
        public DialogUnavailableException(string message) : base(message) { }
    }

    // Requests to RegenerateFM's library in FM24 (the DLL, or the Mac library),
    // on its authenticated loopback port (the bridge port + 1): FM24's own
    // dialogs, progress text, the Mac key prompt and the skin reload. Each
    // request is repeated with the same serial until FM24 answers, so FM24
    // never acts on it twice.
    public static class Dll
    {
        public const int MaxTitle = 250;
        public const int MaxText = 1000;
        public const int MaxLabel = 90;

        private static long Serial() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static UdpClient Connect(int port)
        {
            var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.ReceiveTimeout = 500;
            client.Connect(IPAddress.Loopback, port);
            return client;
        }

        // One send and its reply: the reply text, null after half a second
        // without one. Nothing listening (Linux's refusal, Windows's reset)
        // throws DialogUnavailableException.
        private static string Exchange(UdpClient client, byte[] packet, int replySize = 128)
        {
            try
            {
                client.Send(packet, packet.Length);
                var remote = new IPEndPoint(IPAddress.Any, 0);
                var reply = client.Receive(ref remote);
                return Encoding.ASCII.GetString(reply, 0, Math.Min(reply.Length, replySize));
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut)
            {
                return null;
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused ||
                                                 error.SocketErrorCode == SocketError.ConnectionReset)
            {
                throw new DialogUnavailableException(Text.T("The FM24 native DLL is not listening."));
            }
        }

        private static string Encode(string value, int limit, string what)
        {
            var data = Encoding.UTF8.GetBytes(Collapse(value));
            if (data.Length == 0 || data.Length > limit || Array.IndexOf(data, (byte)0) >= 0)
                throw new ArgumentException("FM24 " + what + " text is empty or too long.");
            return Convert.ToBase64String(data);
        }

        private static string Collapse(string value) => Memory.People.CollapseSpaces(value ?? "");

        private static byte[] DialogPacket(long serial, string token, string kind, string title, string text) =>
            Encoding.ASCII.GetBytes("REGENERATEFM24D 1 " + serial + " " + token + " " + kind + " " +
                                    Encode(title, MaxTitle, "dialog") + " " + Encode(text, MaxText, "dialog"));

        // A choice: version 2 of the dialog request, with its two answers' labels.
        private static byte[] ChoicePacket(long serial, string token, string title, string text, string yes, string extra) =>
            Encoding.ASCII.GetBytes("REGENERATEFM24D 2 " + serial + " " + token + " choice " +
                                    Encode(title, MaxTitle, "dialog") + " " + Encode(text, MaxText, "dialog") + " " +
                                    Encode(yes, MaxLabel, "button") + " " + Encode(extra, MaxLabel, "button"));

        // Shows a dialog in FM24 and returns "yes", "no" or "shown". "confirm"
        // (Yes/No) waits for the answer, up to ``timeout`` seconds (forever when
        // null); "message" (OK) returns "shown" once it is on screen.
        // DialogUnavailableException when FM24 cannot show it.
        public static string ShowDialog(int port, string token, string kind, string title, string text,
                                        double? timeout = null, long? serial = null)
        {
            if (kind != "confirm" && kind != "message" && kind != "progress")
                throw new ArgumentException("Unknown FM24 dialog kind.");
            long number = serial ?? Serial();
            return Await(port, number, kind, DialogPacket(number, token, kind, title, text), timeout);
        }

        // Asks a question in FM24 with two answers and No: "yes" for the first
        // answer (``yes``, the button labelled so), "extra" for the second and
        // "no", waiting up to ``timeout`` seconds (forever when null).
        // DialogUnavailableException when FM24 cannot show it.
        public static string ShowChoice(int port, string token, string title, string text, string yes, string extra,
                                        double? timeout = null, long? serial = null)
        {
            long number = serial ?? Serial();
            return Await(port, number, "choice", ChoicePacket(number, token, title, text, yes, extra), timeout);
        }

        private static string Await(int port, long number, string kind, byte[] packet, double? timeout)
        {
            string prefix = "REGENERATEFM24D " + number + " ";
            bool replied = false, shown = false;
            var clock = Stopwatch.StartNew();
            using var client = Connect(port);
            while (true)
            {
                double waited = clock.Elapsed.TotalSeconds;
                if (!replied && waited > 5)
                    throw new DialogUnavailableException("The FM24 native DLL did not answer the dialog request.");
                if (!shown && waited > 15)
                    throw new DialogUnavailableException("FM24 did not show the dialog.");
                if (shown && timeout != null && waited > timeout)
                    throw new TimeoutException("No answer to the FM24 dialog.");
                string reply = Exchange(client, packet);
                if (reply == null || !reply.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                replied = true;
                string status = reply.Substring(prefix.Length);
                if (status == "busy" || status == "failed")
                    throw new DialogUnavailableException("FM24 could not show the dialog (" + status + ").");
                if (status == "yes" || status == "no" || (kind == "choice" && status == "extra"))
                    return status;
                if (status == "shown")
                {
                    if (kind != "confirm" && kind != "choice")
                        return status;
                    shown = true;
                }
                Thread.Sleep(250);
            }
        }

        // Sets in-game progress text: on the portrait ("portrait"), in the open
        // RegenerateFM dialog ("dialog"), or turning that dialog into an OK
        // message ("finish"). Empty text clears it. DialogUnavailableException
        // when FM24 could not apply it.
        public static void SetProgress(int port, string token, string target, string text, long? serial = null)
        {
            if (target != "portrait" && target != "dialog" && target != "finish")
                throw new ArgumentException("Unknown FM24 progress target.");
            var data = Encoding.UTF8.GetBytes(Collapse(text));
            if (data.Length > MaxText || Array.IndexOf(data, (byte)0) >= 0)
                throw new ArgumentException("FM24 progress text is too long.");
            long number = serial ?? Serial();
            var packet = Encoding.ASCII.GetBytes("REGENERATEFM24P 1 " + number + " " + token + " " + target + " " +
                                                 (data.Length > 0 ? Convert.ToBase64String(data) : "-"));
            string prefix = "REGENERATEFM24P " + number + " ";
            var clock = Stopwatch.StartNew();
            using var client = Connect(port);
            while (clock.Elapsed.TotalSeconds < 5)
            {
                string reply = Exchange(client, packet);
                if (reply == null || !reply.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                string status = reply.Substring(prefix.Length);
                if (status == "done")
                    return;
                if (status == "busy" || status == "failed")
                    throw new DialogUnavailableException("FM24 could not set the progress text (" + status + ").");
                Thread.Sleep(100);
            }
            throw new DialogUnavailableException("FM24 did not apply the progress text.");
        }

        // Asks for the OpenAI key in FM24 (the Mac library's key prompt): what
        // was typed, or null for Cancel. ``text`` may have line breaks.
        // DialogUnavailableException when the library can't show the prompt.
        public static string AskKey(int port, string token, string text, double timeout = 1800, long? serial = null)
        {
            var data = Encoding.UTF8.GetBytes((text ?? "").Trim());
            if (data.Length == 0 || data.Length > MaxText || Array.IndexOf(data, (byte)0) >= 0)
                throw new ArgumentException("FM24 key prompt text is empty or too long.");
            long number = serial ?? Serial();
            var packet = Encoding.ASCII.GetBytes("REGENERATEFM24K 1 " + number + " " + token + " " + Convert.ToBase64String(data));
            string prefix = "REGENERATEFM24K " + number + " ";
            bool replied = false;
            var clock = Stopwatch.StartNew();
            using var client = Connect(port);
            while (true)
            {
                double waited = clock.Elapsed.TotalSeconds;
                if (!replied && waited > 5)
                    throw new DialogUnavailableException("The FM24 native library did not answer the key prompt request.");
                if (waited > timeout)
                    throw new TimeoutException("No answer to FM24's key prompt.");
                string reply = Exchange(client, packet, 1024);
                if (reply == null || !reply.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                replied = true;
                string status = reply.Substring(prefix.Length);
                if (status == "busy")
                    throw new DialogUnavailableException("FM24 is already showing the key prompt.");
                if (status == "cancel")
                    return null;
                if (status.StartsWith("key ", StringComparison.Ordinal))
                {
                    try
                    {
                        return Memory.People.Utf8.GetString(Convert.FromBase64String(status.Substring(4)));
                    }
                    catch (Exception error) when (error is FormatException || error is DecoderFallbackException)
                    {
                        return "";
                    }
                }
                Thread.Sleep(250);
            }
        }

        // Returns once FM24 reports the skin reload finished; throws if it did
        // not. The DLL replies "queued" until its UI thread has completed FM24's
        // own reload-current-skin command. Repeating the same serial polls for
        // the result and never starts a second reload.
        public static void RequestReload(int port, string token, double timeout = 70, long? serial = null)
        {
            long number = serial ?? Serial();
            var packet = Encoding.ASCII.GetBytes("REGENERATEFM24R 1 " + number + " " + token);
            string prefix = "REGENERATEFM24R " + number + " ";
            var clock = Stopwatch.StartNew();
            bool answered = false;
            using var client = Connect(port);
            while (clock.Elapsed.TotalSeconds < timeout)
            {
                string reply;
                try
                {
                    reply = Exchange(client, packet);
                }
                catch (DialogUnavailableException)
                {
                    // Nothing listening yet; FM24 may be starting.
                    Thread.Sleep(500);
                    continue;
                }
                if (reply == null || !reply.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                string status = reply.Substring(prefix.Length);
                answered = true;
                if (status == "done")
                    return;
                if (status == "failed")
                    throw new InvalidOperationException(Text.T("FM24 could not reload its skin in game"));
                if (status == "busy")
                    throw new InvalidOperationException(Text.T("FM24 is already reloading its skin"));
                Thread.Sleep(250);
            }
            throw new TimeoutException(answered ? Text.T("FM24 did not finish reloading its skin")
                                                : Text.T("The FM24 native DLL did not answer the reload request"));
        }

        // RegenerateFM's own text in FM24 (its menu rows, dialog buttons, the
        // messages the library shows by itself and the Mac key prompt), in
        // Text.Language. The order is the library's LABEL_ enum
        // (regeneratefm24.c, regeneratefm24_macos.c); English must stay exactly
        // the library's defaults. "{0}"/"{1}" in the last one are the pasted
        // key's length and its last four characters, which the Mac prompt fills.
        public static string[] Labels() => new[]
        {
            Text.T("Generate missing squad faces…"),
            Text.T("Generate missing staff faces…"),
            Text.T("Generate new face"),
            Text.T("Update face"),
            Text.T("No"),
            Text.T("OK"),
            Text.T("Stop after this face"),
            Text.T("RegenerateFM could not tell which menu item was chosen, so nothing was generated. Choose it again."),
            Text.T("RegenerateFM could not start its network bridge. Restart FM24 and check regeneratefm24.log."),
            Text.T("RegenerateFM could not open its local connection. Restart FM24 and check regeneratefm24.log."),
            Text.T("RegenerateFM is already processing a request. Please wait for it to finish."),
            Text.T("RegenerateFM received this click too late to be sure which screen was open, so nothing was generated. Choose the menu item again."),
            Text.T("RegenerateFM: credits or your OpenAI API key"),
            Text.T("Save key"),
            Text.T("Cancel"),
            Text.T("Get a key…"),
            Text.T("Buy credits / Sign in"),
            Text.T("{0} characters, ending …{1}"),
        };

        // What the library takes: a label up to 511 UTF-8 bytes, and all of them
        // (newline-separated, before base64) up to 2849.
        public const int MaxLabelBytes = 500;
        public const int MaxLabelsBytes = 2800;

        // "REGENERATEFM24L 1 <serial> <token> <base64 of the labels joined by
        // newlines>". A label that is too long goes empty, which the library
        // shows in English; so do the longest ones while all of them are.
        internal static byte[] LabelsPacket(long serial, string token, string[] labels)
        {
            var data = new byte[labels.Length][];
            for (int i = 0; i < labels.Length; i++)
            {
                data[i] = Encoding.UTF8.GetBytes(Collapse(labels[i]));
                if (data[i].Length > MaxLabelBytes || Array.IndexOf(data[i], (byte)0) >= 0)
                    data[i] = Array.Empty<byte>();
            }
            while (true)
            {
                int total = labels.Length - 1, longest = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    total += data[i].Length;
                    if (data[i].Length > data[longest].Length)
                        longest = i;
                }
                if (total <= MaxLabelsBytes || data[longest].Length == 0)
                    break;
                data[longest] = Array.Empty<byte>();
            }
            var joined = new System.IO.MemoryStream();
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0)
                    joined.WriteByte((byte)'\n');
                joined.Write(data[i], 0, data[i].Length);
            }
            return Encoding.ASCII.GetBytes("REGENERATEFM24L 1 " + serial + " " + token + " " + Convert.ToBase64String(joined.ToArray()));
        }

        // Sends Labels() to FM24's library: true once it has taken them, false
        // if it didn't answer within ``timeout`` seconds (not running yet, or a
        // library from before translations, which keeps its English). Call it
        // whenever Text.Language changes and once FM24's library is up.
        public static bool SendLabels(int port, string token, double timeout = 5, long? serial = null)
        {
            long number = serial ?? Serial();
            var packet = LabelsPacket(number, token, Labels());
            string expected = "REGENERATEFM24L " + number + " done";
            var clock = Stopwatch.StartNew();
            try
            {
                using var client = Connect(port);
                while (clock.Elapsed.TotalSeconds < timeout)
                {
                    string reply;
                    try
                    {
                        reply = Exchange(client, packet);
                    }
                    catch (DialogUnavailableException)
                    {
                        // Nothing listening yet; FM24 may be starting.
                        Thread.Sleep(500);
                        continue;
                    }
                    if (reply == expected)
                        return true;
                }
            }
            catch (SocketException)
            {
            }
            return false;
        }

        // Text in an FM24 OK dialog, or ``fallback(text)`` if FM24 can't show it.
        public static void Notify(int? port, string token, string text, Action<string> fallback)
        {
            if (port != null)
            {
                try
                {
                    ShowDialog(port.Value, token, "message", "RegenerateFM", ClipText(text));
                    return;
                }
                catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException || error is SocketException)
                {
                }
            }
            fallback(text);
        }

        // ``text`` cut to what FM24's dialog takes.
        public static string ClipText(string text)
        {
            var data = Encoding.UTF8.GetBytes(Collapse(text));
            if (data.Length <= MaxText)
                return text;
            // Drop a character cut in half.
            int end = MaxText - 3;
            while (end > 0 && (data[end] & 0xC0) == 0x80)
                end--;
            return Encoding.UTF8.GetString(data, 0, end) + "…";
        }

        // The squad batch's progress: Stopped once the player chooses Stop.
        public interface IBatchProgress : IDisposable
        {
            ManualResetEventSlim Stopped { get; }
            void Update(string message);
            void Finish(string text, Action<string> fallback);
        }

        // Squad batch progress in an FM24 dialog whose one button is "Stop after
        // this face". FM24's skin reload after each face closes the dialog, so
        // the next update opens a new one. The constructor throws
        // DialogUnavailableException if FM24 cannot show it.
        public sealed class Progress : IBatchProgress
        {
            private readonly int _port;
            private readonly string _token, _title;
            private readonly object _lock = new object();
            private readonly ManualResetEventSlim _closed = new ManualResetEventSlim();
            private byte[] _packet;
            private string _prefix;

            public Progress(int port, string token, string title, string text)
            {
                _port = port;
                _token = token;
                _title = title;
                Show(text);
                new Thread(Watch) { IsBackground = true }.Start();
            }

            public ManualResetEventSlim Stopped { get; } = new ManualResetEventSlim();

            private string Exchange(byte[] packet, string prefix)
            {
                try
                {
                    using var client = Connect(_port);
                    string reply = Dll.Exchange(client, packet);
                    return reply != null && reply.StartsWith(prefix, StringComparison.Ordinal) ? reply.Substring(prefix.Length) : null;
                }
                catch (Exception error) when (error is DialogUnavailableException || error is SocketException)
                {
                    return null;
                }
            }

            private void Show(string text)
            {
                long serial = Serial();
                var packet = DialogPacket(serial, _token, "progress", _title, ClipText(text));
                string prefix = "REGENERATEFM24D " + serial + " ";
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < 15)
                {
                    string status = Exchange(packet, prefix);
                    if (status == "shown")
                    {
                        _packet = packet;
                        _prefix = prefix;
                        return;
                    }
                    if (status == "busy" || status == "failed" || status == "closed")
                        break;
                    Thread.Sleep(250);
                }
                throw new DialogUnavailableException("FM24 did not show the progress dialog.");
            }

            private void Watch()
            {
                while (!_closed.Wait(500))
                {
                    string status;
                    lock (_lock)
                        status = _packet != null ? Exchange(_packet, _prefix) : null;
                    if (status == "yes")
                    {
                        Stopped.Set();
                        return;
                    }
                }
            }

            public void Update(string message)
            {
                if (Stopped.IsSet || _closed.IsSet)
                    return;
                lock (_lock)
                {
                    try
                    {
                        SetProgress(_port, _token, "dialog", message);
                    }
                    catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException)
                    {
                        // Gone: answered (Stop), or closed by a skin reload.
                        if (_packet != null && Exchange(_packet, _prefix) == "yes")
                        {
                            Stopped.Set();
                            return;
                        }
                        try
                        {
                            Show(message);
                        }
                        catch (DialogUnavailableException)
                        {
                        }
                    }
                }
            }

            // Turns the progress dialog into an OK message with ``text``, or shows
            // a new message if FM24 has closed it (a skin reload or Stop).
            public void Finish(string text, Action<string> fallback)
            {
                _closed.Set();
                lock (_lock)
                {
                    try
                    {
                        if (Stopped.IsSet)
                            throw new DialogUnavailableException("the progress dialog was answered");
                        SetProgress(_port, _token, "finish", ClipText(text));
                    }
                    catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException)
                    {
                        Notify(_port, _token, text, fallback);
                    }
                }
            }

            public void Dispose() => _closed.Set();
        }
    }
}
