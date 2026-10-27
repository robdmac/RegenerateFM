using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RegenerateFM.Desktop.FM24.Bridge
{
    // What one face command came to, for the log and FM24's dialogs.
    public sealed class FaceResult
    {
        public bool Ok;
        public bool Preview;
        public bool Cancelled;
        public bool Refreshed;
        public bool Shown;        // the error or warning is already in FM24's progress dialog
        public string PlayerId;
        public string Prompt;
        public string Reference;  // where Update face's starting picture came from
        public string Error;
        public string Warning;

        public static FaceResult Failed(string error, bool shown = false) => new FaceResult { Error = error, Shown = shown };

        // One log line for the result.
        public string Describe()
        {
            if (!Ok)
                return "FM24 generation failed: " + (Error ?? "unknown error");
            if (Preview)
                return "FM24 profile preview for person " + PlayerId + ":\n" + Prompt +
                       (Reference != null ? "\nExisting portrait: " + Reference : "");
            if (Refreshed)
                return "FM24 portrait loaded for person " + PlayerId + ".";
            return "FM24 portrait saved; " + (Warning ?? "reload needs attention");
        }
    }

    // The authenticated loopback bridge from FM24's menus (the DLL, or the Mac
    // library) to the companion. The library reports the person ID shown in
    // the clicked screen's header, read from FM24's memory inside the menu
    // callback, and the click time. Without screen IDs the Mac library reports
    // "r<reference>" instead: the screen's object browser reference, which
    // ``Resolve`` turns into the person's ID. A command older than MaxClickAge
    // is refused as defence in depth; the person binding is what prevents a
    // delayed command from targeting a newly opened profile.
    public sealed class Listener : IDisposable
    {
        public const double MaxClickAge = .75;
        public const double ClockTolerance = .25;

        private readonly string _token;
        private readonly UdpClient _server;
        private readonly object _busyLock = new object();
        // Set by Dispose, which then wakes the receive thread (see Dispose).
        private volatile bool _closing;
        private bool _busy;
        // Per game run (the DLL's start time, not the process ID, which Wine
        // reuses after a restart): the last serial and the reply sent for it.
        // The DLL retransmits an unanswered click with the same serial; it gets
        // the same reply and never starts a second request.
        private readonly Dictionary<string, (long Serial, byte[] Reply)> _lastReply = new Dictionary<string, (long, byte[])>();

        // A single face for the person the click named: (person ID, "generate" or "update").
        public Func<string, string, FaceResult> Generate;
        // The squad command.
        public Action Squad;
        // The staff command, for the club the screen shows.
        public Action Staff;
        // Whether the person shown in FM24's portrait popup has a face (for its Update face row).
        public Func<string, bool> HasFace;
        // A Mac object browser reference's person ID; ``wait`` false while a menu opens.
        public Func<uint, bool, string> Resolve;
        // Asks for a missing key first (the Mac's prompt in FM24); false if the player cancels.
        public Func<bool> KeyReady;
        public Action<string> Notify = text => Console.WriteLine("RegenerateFM: " + text);
        public Action<string> Log = text => Console.WriteLine(text);
        public Func<double> Clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        // Starts a click's work (replaced by tests).
        public Action<ThreadStart> Start = work => new Thread(work) { IsBackground = true }.Start();

        public Listener(int port, string token)
        {
            _token = token;
            try
            {
                _server = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            }
            catch (SocketException error)
            {
                throw new InvalidOperationException("FM24 native bridge port 127.0.0.1:" + port + " is unavailable (" +
                                                    error.Message + "). Is another companion already running?");
            }
            if (OperatingSystem.IsWindows())
            {
                // An answer to a DLL that has gone shouldn't make later receives fail (SIO_UDP_CONNRESET).
                _server.Client.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null);
            }
        }

        public int Port => ((IPEndPoint)_server.Client.LocalEndPoint).Port;

        public void Run()
        {
            new Thread(Receive) { IsBackground = true, Name = "fm24-native-bridge" }.Start();
            Log("FM24 native menu bridge listening on 127.0.0.1:" + Port + ".");
        }

        private void Receive()
        {
            while (!_closing)
            {
                byte[] packet;
                var peer = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    packet = _server.Receive(ref peer);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException error)
                {
                    if (_server.Client == null)
                        return;
                    Log("FM24 native bridge receive error (continuing): " + error.Message);
                    continue;
                }
                if (_closing)
                    return;
                try
                {
                    Handle(Encoding.ASCII.GetString(packet), peer);
                }
                catch (Exception error) when (error is SocketException || error is IOException)
                {
                    // An IOException here is the log itself failing (a full disk, say).
                    try
                    {
                        Log("FM24 native bridge reply failed (continuing): " + error.Message);
                    }
                    catch (IOException)
                    {
                    }
                }
                catch (Exception error) when (error is FormatException || error is OverflowException || error is ArgumentException)
                {
                }
            }
        }

        private static bool IsDecimal(string value)
        {
            if (value.Length == 0)
                return false;
            foreach (char c in value)
                if (c < '0' || c > '9')
                    return false;
            return true;
        }

        private static bool PersonField(string value) =>
            IsDecimal(value) || (value.Length > 1 && value[0] == 'r' && IsDecimal(value.Substring(1)));

        private bool TokenMatches(string key) =>
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(_token));

        private void Send(string text, IPEndPoint peer)
        {
            var data = Encoding.ASCII.GetBytes(text);
            _server.Send(data, data.Length, peer);
        }

        private void Handle(string text, IPEndPoint peer)
        {
            var fields = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (!IPAddress.IsLoopback(peer.Address) || !peer.Address.Equals(IPAddress.Loopback))
                return;
            if (fields.Length > 0 && fields[0] == "REGENERATEFM24F")
            {
                AnswerFace(fields, peer);
                return;
            }
            if (fields.Length != 8)
                return;
            string magic = fields[0], version = fields[1], pid = fields[2], serial = fields[3], action = fields[4],
                   clicked = fields[5], person = fields[6], key = fields[7];
            if (magic != "REGENERATEFM24" || version != "5" || !IsDecimal(pid) || !IsDecimal(serial) || !IsDecimal(clicked) ||
                !PersonField(person) || (action != "profile" && action != "update" && action != "squad" && action != "staff") || !TokenMatches(key))
                return;
            long sequence = long.Parse(serial, CultureInfo.InvariantCulture);
            _lastReply.TryGetValue(pid, out var previous);
            if (sequence == previous.Serial && previous.Reply != null)
            {
                _server.Send(previous.Reply, previous.Reply.Length, peer);
                return;
            }
            if (sequence < previous.Serial)
                return;
            // Bound state even if a local client with the token sends new process IDs.
            if (_lastReply.Count >= 32 && !_lastReply.ContainsKey(pid))
                _lastReply.Clear();
            double age = Clock() - long.Parse(clicked, CultureInfo.InvariantCulture) / 1000.0;
            bool stale = !(-ClockTolerance <= age && age <= MaxClickAge);
            bool accepted = false;
            if (!stale)
                lock (_busyLock)
                    if (!_busy)
                        accepted = _busy = true;
            string status = stale ? "stale" : accepted ? "accepted" : "busy";
            if (stale)
                Log("FM24 native " + action + " command arrived " + age.ToString("0.00", CultureInfo.InvariantCulture) +
                    "s after the click; refused because the screen may have changed. No image request was made.");
            var reply = Encoding.ASCII.GetBytes("REGENERATEFM24 ACK " + serial + " " + status);
            _lastReply[pid] = (sequence, reply);
            try
            {
                _server.Send(reply, reply.Length, peer);
            }
            catch (SocketException error)
            {
                // The DLL retransmits and receives the cached reply; the accepted
                // click must still run and release the busy lock.
                Log("FM24 native bridge reply failed (continuing): " + error.Message);
            }
            if (!accepted)
                return;
            string bound = (action == "profile" || action == "update") && person != "0" ? person : null;
            try
            {
                Start(() => Process(action, bound));
            }
            catch (Exception error) when (error is OutOfMemoryException || error is ThreadStateException || error is InvalidOperationException)
            {
                Release();
                string message = "Could not start the FM24 face request worker: " + error.Message + "; no image request was made.";
                Log(message);
                Notify(message);
            }
        }

        private void Release()
        {
            lock (_busyLock)
                _busy = false;
        }

        private void Process(string action, string bound)
        {
            try
            {
                if (action == "squad" || action == "staff")
                {
                    // Roster, portrait state and prompts come from game memory.
                    (action == "squad" ? Squad : Staff)?.Invoke();
                    return;
                }
                // The companion reads the open profile from memory and refuses the
                // request unless it is still this person.
                if (bound == null)
                    throw new InvalidOperationException("FM24 did not report which person's menu was chosen; no image request was made.");
                // FM24 for macOS asks for a missing key first, before the request.
                if (KeyReady != null && !KeyReady())
                {
                    Log("No OpenAI key was saved; no image request was made.");
                    return;
                }
                if (bound.StartsWith("r", StringComparison.Ordinal))
                {
                    if (Resolve == null)
                        throw new InvalidOperationException("Enable screen IDs in FM24's preferences; no image request was made.");
                    bound = Resolve(uint.Parse(bound.Substring(1), CultureInfo.InvariantCulture), true);
                }
                var result = Generate(bound, action == "update" ? "update" : "generate");
                Log(result.Describe());
                if (result.Shown)
                {
                    // Already in FM24's progress dialog.
                }
                else if (!result.Ok && !result.Cancelled)
                    Notify(result.Error ?? "RegenerateFM could not generate this portrait.");
                else if (result.Ok && !result.Preview && !result.Refreshed)
                    Notify(result.Warning ?? "The portrait was saved, but FM24 did not reload it.");
            }
            catch (Exception error)
            {
                Log("FM24 native menu failed: " + error.Message);
                Notify(error.Message);
            }
            finally
            {
                Release();
            }
        }

        // How long the DLL's menu waits for AnswerFace (FACE_TIMEOUT_MS).
        private const int FaceWaitMs = 30;

        // "REGENERATEFM24F 1 <serial> <person> <token>": does the person shown in
        // FM24's portrait popup have a face? The DLL greys out Update face on
        // "no" and leaves it enabled without an answer.
        private void AnswerFace(string[] fields, IPEndPoint peer)
        {
            if (fields.Length != 5 || fields[1] != "1" || !IsDecimal(fields[2]) || !PersonField(fields[3]) ||
                !TokenMatches(fields[4]) || HasFace == null)
                return;
            string person = fields[3];
            bool has;
            var clock = Stopwatch.StartNew();
            try
            {
                if (person.StartsWith("r", StringComparison.Ordinal))
                {
                    if (Resolve == null)
                        return;
                    person = Resolve(uint.Parse(person.Substring(1), CultureInfo.InvariantCulture), false);
                }
                has = HasFace(person);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                Log("FM24 face check failed (Update face stays enabled): " + error.Message);
                return;
            }
            Send("REGENERATEFM24F " + fields[2] + " " + (has ? "yes" : "no"), peer);
            if (clock.ElapsedMilliseconds > FaceWaitMs)
                Log("FM24 face check took " + clock.ElapsedMilliseconds + " ms, longer than FM24's menu waits (" + FaceWaitMs +
                    " ms), so Update face stayed enabled.");
        }

        public void Dispose()
        {
            // On macOS closing the socket doesn't interrupt a blocked Receive, and
            // the close waits for it, so an empty datagram ends the receive first.
            _closing = true;
            try
            {
                using var wake = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                wake.Send(Array.Empty<byte>(), 0, new IPEndPoint(IPAddress.Loopback, Port));
            }
            catch (Exception error) when (error is SocketException || error is ObjectDisposedException)
            {
            }
            _server.Dispose();
        }
    }
}
