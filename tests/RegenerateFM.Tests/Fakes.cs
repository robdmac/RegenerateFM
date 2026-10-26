using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using RegenerateFM.Desktop.FM24;
using RegenerateFM.Desktop.FM24.Memory;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace RegenerateFM.Tests
{
    internal static class Setup
    {
        public static readonly string Data = Path.Combine(Path.GetTempPath(), "regeneratefm-tests-" + Guid.NewGuid().ToString("N"));

        // The tests never read or write the player's own key or settings.
        [ModuleInitializer]
        internal static void Isolate()
        {
            KeyStore.FolderOverride = Data;
            Credits.Delay = (_, _) => System.Threading.Tasks.Task.CompletedTask;
            RegenerateFM.Desktop.Linux.UnityLibraries.Download = _ => throw new System.IO.IOException("The tests don't download.");
        }
    }

    // A test of the Linux installer or Steam's launch options, which Windows
    // doesn't use (it also runs on macOS, where the installer's code is shared).
    public sealed class NotOnWindowsFactAttribute : Xunit.FactAttribute
    {
        public NotOnWindowsFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "Windows has no Steam launch option or Linux installer.";
        }
    }

    // Sparse little-endian process memory, at byte level. Missing bytes are
    // unreadable unless ``zeros``.
    public sealed class FakeProcess : GameProcess
    {
        private readonly Dictionary<long, byte> _bytes = new Dictionary<long, byte>();
        private readonly bool _zeros;

        public FakeProcess(Layout layout = null, long image = 0x140000000, bool zeros = false, int pid = 1) : base(pid, layout ?? Layout.Windows)
        {
            Image = image;
            _zeros = zeros;
        }

        public long Image;
        public string ExecutablePath = "C:/Games/Football Manager 2024/fm.exe";
        public List<(long, long)> Regions;
        public int Reads, Scans;

        public override long ImageBase => Image;
        public override string Executable => ExecutablePath;

        public FakeProcess Put(long address, byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
                _bytes[address + i] = data[i];
            return this;
        }

        public FakeProcess Q(long address, params long[] values)
        {
            var data = new byte[8 * values.Length];
            for (int i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8 * i), values[i]);
            return Put(address, data);
        }

        public FakeProcess I(long address, params uint[] values)
        {
            var data = new byte[4 * values.Length];
            for (int i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4 * i), values[i]);
            return Put(address, data);
        }

        // An FM {u32 length, UTF-8, 0} string.
        public FakeProcess Text(long address, string text)
        {
            var data = Encoding.UTF8.GetBytes(text);
            return I(address, (uint)data.Length).Put(address + 4, data.Concat(new byte[] { 0 }).ToArray());
        }

        public override int ReadInto(long address, Span<byte> buffer)
        {
            Interlocked.Increment(ref Reads);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (_bytes.TryGetValue(address + i, out byte value))
                    buffer[i] = value;
                else if (_zeros)
                    buffer[i] = 0;
                else if (i == 0)
                    throw new IOException("Bad address");
                else
                    return i;
            }
            return buffer.Length;
        }

        // A scan sees zeros where nothing was put, as the game's dense memory.
        public override int ScanInto(long address, Span<byte> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = _bytes.TryGetValue(address + i, out byte value) ? value : (byte)0;
            Interlocked.Increment(ref Scans);
            return buffer.Length;
        }

        public override IEnumerable<(long Start, long End)> WritableRegions()
        {
            if (Regions != null)
                return Regions;
            // The bytes put, in regions split where there is a large gap.
            var regions = new List<(long, long)>();
            long start = -1, end = -1;
            foreach (long address in _bytes.Keys.OrderBy(address => address))
            {
                if (start >= 0 && address - end > 0x10000)
                {
                    regions.Add((start, end));
                    start = -1;
                }
                if (start < 0)
                    start = address;
                end = address + 1;
            }
            if (start >= 0)
                regions.Add((start, end));
            return regions;
        }

        public override int ScanChunk => 1 << 20;
    }

    // Answers the companion's dialog, progress, key and reload packets like
    // the FM24 DLL: the scripted statuses, one per packet (the last repeats).
    public sealed class FakeDll : IDisposable
    {
        private readonly UdpClient _socket;
        private volatile bool _done;
        public readonly ConcurrentQueue<string[]> Packets = new ConcurrentQueue<string[]>();
        public Func<string[], string> Answer;
        private readonly List<string> _statuses;

        public FakeDll(params string[] statuses)
        {
            _statuses = statuses.ToList();
            _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _socket.Client.ReceiveTimeout = 200;
            new Thread(Serve) { IsBackground = true }.Start();
        }

        public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint).Port;
        public List<string[]> Received => Packets.ToList();

        public void Script(params string[] statuses)
        {
            lock (_statuses)
            {
                _statuses.Clear();
                _statuses.AddRange(statuses);
            }
        }

        private void Serve()
        {
            while (!_done)
            {
                var peer = new IPEndPoint(IPAddress.Any, 0);
                byte[] packet;
                try
                {
                    packet = _socket.Receive(ref peer);
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                var fields = Encoding.ASCII.GetString(packet).Split(' ');
                Packets.Enqueue(fields);
                string status;
                if (Answer != null)
                {
                    try
                    {
                        status = Answer(fields);
                    }
                    catch (Exception error) when (error is IndexOutOfRangeException || error is FormatException)
                    {
                        continue;  // not a packet this test answers
                    }
                }
                else
                    lock (_statuses)
                    {
                        if (_statuses.Count == 0)
                            continue;
                        status = _statuses[0];
                        if (_statuses.Count > 1)
                            _statuses.RemoveAt(0);
                    }
                if (status == null)
                    continue;
                var reply = Encoding.ASCII.GetBytes(fields[0] + " " + fields[2] + " " + status);
                try
                {
                    _socket.Send(reply, reply.Length, peer);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            _done = true;
            _socket.Dispose();
        }
    }

    // A stand-in for OpenAI's image API and the RegenerateFM credits service:
    // records each request and answers it. A small HTTP/1.1 server on a
    // socket rather than HttpListener, which on Windows goes through http.sys:
    // that wants a URL reservation for a non-administrator, and Wine's drops
    // uploads.
    public sealed class StandIn : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        public readonly ConcurrentQueue<(string Method, string Path, string Authorization, string ContentType, byte[] Body, string Agent)> Requests =
            new ConcurrentQueue<(string, string, string, string, byte[], string)>();
        public byte[] Image;
        public string GoodKey = "sk-good-key-0123456789abcdef";
        public int Pending = 1;
        public bool Expire;
        public string LinkHost;
        public Func<Dictionary<string, object>, Dictionary<string, object>> StartAnswer = answer => answer;  // /device/start's, changed
        public string Token = "rfm_" + new string('t', 43);
        public Func<string, int?> Status = _ => null;  // an error status for a path, or null

        public StandIn(byte[] image = null)
        {
            Image = image ?? Pngs.Make(1024);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            new Thread(Serve) { IsBackground = true }.Start();
        }

        public int Port { get; }
        public string Url => "http://127.0.0.1:" + Port;
        public List<(string Method, string Path, string Authorization, string ContentType, byte[] Body, string Agent)> Received => Requests.ToList();

        private void Serve()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception error) when (error is SocketException || error is ObjectDisposedException || error is InvalidOperationException)
                {
                    return;
                }
                new Thread(() => Answer(client)) { IsBackground = true }.Start();
            }
        }

        // One request per connection, answered with Connection: close.
        private void Answer(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = Read(stream);
                    if (request == null)
                        return;
                    Requests.Enqueue(request.Value);
                    var (status, value) = Handle(request.Value);
                    var body = JsonSerializer.SerializeToUtf8Bytes(value);
                    string reason = status switch { 200 => "OK", 401 => "Unauthorized", 404 => "Not Found", 410 => "Gone", _ => "Error" };
                    stream.Write(Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: application/json\r\n" +
                                                         "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n"));
                    stream.Write(body);
                }
                catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException)
                {
                }
            }
        }

        // The request's line, headers and Content-Length body, or null if the
        // connection closed first.
        private static (string Method, string Path, string Authorization, string ContentType, byte[] Body, string Agent)? Read(Stream stream)
        {
            var head = new List<byte>();
            while (head.Count < 4 || head[^4] != '\r' || head[^3] != '\n' || head[^2] != '\r' || head[^1] != '\n')
            {
                int next = stream.ReadByte();
                if (next < 0)
                    return null;
                head.Add((byte)next);
            }
            string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
            string[] start = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            var body = new byte[headers.TryGetValue("Content-Length", out string length) ? int.Parse(length) : 0];
            stream.ReadExactly(body);
            string path = start[1].Split('?')[0];
            return (start[0], path, headers.GetValueOrDefault("Authorization"), headers.GetValueOrDefault("Content-Type"), body,
                    headers.GetValueOrDefault("User-Agent"));
        }

        private (int, object) Handle((string Method, string Path, string Authorization, string ContentType, byte[] Body, string Agent) request)
        {
            string path = request.Path;
            string auth = request.Authorization;
            if (Status(path) is int error)
                return (error, new { error = new { message = "Stand-in error " + error + "." } });
            if (request.Method == "GET" && path.EndsWith("/models"))
            {
                bool good = auth == "Bearer " + GoodKey || auth == "Bearer " + Token;
                return (good ? 200 : 401, good ? (object)new { data = Array.Empty<object>() } : new { error = new { message = "Incorrect API key" } });
            }
            if (path == "/device/start")
            {
                return (200, StartAnswer(new Dictionary<string, object>
                {
                    ["device_code"] = new string('d', 43), ["user_code"] = "WXYZ-K7P3",
                    ["verification_uri_complete"] = (LinkHost ?? Url) + "/link?code=WXYZ-K7P3", ["expires_in"] = 600, ["interval"] = 2,
                }));
            }
            if (path == "/device/poll")
            {
                if (Expire)
                    return (410, new { error = new { message = "expired" } });
                if (Pending > 0)
                {
                    Pending--;
                    return (200, new { status = "pending", interval = 2 });
                }
                return (200, new { status = "approved", token = Token, email = "player@example.com", credits = 75 });
            }
            if (path.EndsWith("/images/generations") || path.EndsWith("/images/edits"))
            {
                if ((auth ?? "").Contains("bad-key"))
                    return (401, new { error = new { message = "Incorrect API key provided: sk-bad-key." } });
                return (200, new { data = new[] { new { b64_json = Convert.ToBase64String(Image) } } });
            }
            return (404, new { });
        }

        public void Dispose() => _listener.Stop();
    }

    public static class Pngs
    {
        // A size × size RGBA PNG with a pattern and partly transparent pixels.
        public static byte[] Make(int size, byte red = 200)
        {
            var rgba = new byte[size * size * 4];
            for (int y = size / 4; y < 3 * size / 4; y++)
                for (int x = size / 4; x < 3 * size / 4; x++)
                {
                    int i = (y * size + x) * 4;
                    rgba[i] = red;
                    rgba[i + 1] = (byte)(x * 7 % 256);
                    rgba[i + 2] = (byte)(y * 3 % 256);
                    rgba[i + 3] = (byte)((x / 64 + y / 64) % 2 == 1 ? 255 : 128);
                }
            return Png.Encode(rgba, size, size, bottomUp: false);
        }
    }
}
