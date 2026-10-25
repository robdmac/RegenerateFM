using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace RegenerateFM.Desktop.FM24.Memory
{
    // FM24 for Mac. macOS doesn't let another program read FM24's memory: the
    // game is signed with the hardened runtime and without get-task-allow, so
    // task_for_pid fails even for root. RegenerateFM's library inside FM24
    // (native/fm24/regeneratefm24_macos.c) serves reads instead, on loopback
    // TCP at the bridge port + 2, after the same private token as the click
    // bridge. Its port and token are in regeneratefm24.ini beside fm.app.
    public sealed class MacProcess : GameProcess
    {
        private const int MinRegion = 64;
        private readonly object _lock = new object();
        private readonly string _executable;
        private TcpClient _client;
        private NetworkStream _stream;
        private (long Base, byte[] Uuid)? _image;

        public MacProcess(int pid) : base(pid, Layout.MacOS)
        {
            _executable = ExecutablePath(pid);
            if (_executable == null || !_executable.EndsWith(Layout.MacOS.ExecutableSuffix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Process " + pid + " isn't FM24.");
        }

        public override int ScanChunk => 8 << 20;

        public override string Executable => _executable;

        private void Connect()
        {
            // …/Football Manager 2024/fm.app/Contents/MacOS/fm
            string game = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_executable))));
            var (port, token) = Settings(game);
            try
            {
                _client = new TcpClient();
                _client.Connect("127.0.0.1", port + 2);
                _client.ReceiveTimeout = _client.SendTimeout = 10000;
            }
            catch (SocketException error)
            {
                throw new IOException("FM24 isn't running with RegenerateFM's library (start it with run_regeneratefm24.sh).", error);
            }
            _stream = _client.GetStream();
            _stream.Write(Encoding.ASCII.GetBytes("REGENERATEFM24M 1 " + token + "\n"));
            if (Encoding.ASCII.GetString(Exactly(3)) != "OK\n")
                throw new IOException("RegenerateFM's library in FM24 refused the connection.");
        }

        private static (int, string) Settings(string game)
        {
            var values = new Dictionary<string, string>();
            string text;
            try
            {
                text = File.ReadAllText(Path.Combine(game, "regeneratefm24.ini"), Encoding.ASCII);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new IOException("RegenerateFM isn't installed in " + game + " (no regeneratefm24.ini).", error);
            }
            foreach (string line in text.Split('\n'))
            {
                int equals = line.IndexOf('=');
                if (equals > 0)
                    values[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
            }
            if (!values.TryGetValue("port", out string portText) || !int.TryParse(portText, out int port))
                throw new IOException("regeneratefm24.ini has no valid port.");
            values.TryGetValue("token", out string token);
            if (port < 1024 || port > 65533 || (token ?? "").Length < 40)
                throw new IOException("regeneratefm24.ini has no valid port or token.");
            return (port, token);
        }

        private byte[] Exactly(int size)
        {
            var data = new byte[size];
            Fill(data);
            return data;
        }

        private void Fill(Span<byte> data)
        {
            int read = 0;
            while (read < data.Length)
            {
                int part = _stream.Read(data.Slice(read));
                if (part == 0)
                    throw new IOException("FM24 closed RegenerateFM's memory connection.");
                read += part;
            }
        }

        // Calls the library, reconnecting once if FM24 dropped the connection.
        private T Call<T>(Func<T> request)
        {
            for (int attempt = 0; ; attempt++)
            {
                lock (_lock)
                {
                    try
                    {
                        if (_stream == null)
                            Connect();
                        return request();
                    }
                    catch (UnreadableException)
                    {
                        throw;
                    }
                    catch (IOException) when (attempt == 0)
                    {
                        Disconnect();
                    }
                    catch (IOException)
                    {
                        Disconnect();
                        throw;
                    }
                }
            }
        }

        private void Disconnect()
        {
            try { _client?.Dispose(); } catch (SocketException) { }
            _client = null;
            _stream = null;
        }

        private sealed class UnreadableException : IOException
        {
            public UnreadableException(string message) : base(message) { }
        }

        private (long, byte[]) Image()
        {
            if (_image == null)
                _image = Call(() =>
                {
                    _stream.Write(new[] { (byte)'B' });
                    var data = Exactly(24);
                    return (BinaryPrimitives.ReadInt64LittleEndian(data), data[8..]);
                });
            return _image.Value;
        }

        public override long ImageBase
        {
            get
            {
                try
                {
                    return Image().Item1;
                }
                catch (IOException error)
                {
                    throw new InvalidDataException("No FM24 with RegenerateFM's library in process " + Pid + ": " + error.Message);
                }
            }
        }

        public override IEnumerable<(long Start, long End)> WritableRegions()
        {
            var regions = Call(() =>
            {
                _stream.Write(new[] { (byte)'M' });
                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(Exactly(4));
                var data = Exactly(16 * count);
                var found = new List<(long, long)>();
                for (int i = 0; i < count; i++)
                    found.Add((BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16 * i)),
                               BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16 * i + 8))));
                return found;
            });
            return regions.FindAll(region => region.Item2 - region.Item1 >= MinRegion);
        }

        public override int ReadInto(long address, Span<byte> buffer)
        {
            int size = buffer.Length;
            var data = Call(() =>
            {
                var request = new byte[13];
                request[0] = (byte)'R';
                BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(1), address);
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(9), (uint)size);
                _stream.Write(request);
                uint status = BinaryPrimitives.ReadUInt32LittleEndian(Exactly(4));
                if (status != 0)
                    throw new UnreadableException("FM24 memory at 0x" + address.ToString("x") + " is unreadable (Mach error " + status + ").");
                return Exactly(size);
            });
            data.CopyTo(buffer);
            return data.Length;
        }

        public override void Dispose()
        {
            lock (_lock)
                Disconnect();
        }

        public static string ExecutablePath(int pid)
        {
            var path = new byte[4096];
            int length = proc_pidpath(pid, path, (uint)path.Length);
            return length > 0 ? Encoding.UTF8.GetString(path, 0, length) : null;
        }

        // (process ID, executable path) for every process this user can see.
        public static IEnumerable<(int Pid, string Path)> Running()
        {
            int count = proc_listallpids(null, 0);
            var pids = new int[Math.Max(count, 0) + 64];
            count = proc_listallpids(pids, pids.Length * sizeof(int));
            var found = new List<(int, string)>();
            for (int i = 0; i < Math.Min(Math.Max(count, 0), pids.Length); i++)
            {
                string path = pids[i] > 0 ? ExecutablePath(pids[i]) : null;
                if (path != null)
                    found.Add((pids[i], path));
            }
            return found;
        }

        [DllImport("/usr/lib/libproc.dylib")]
        private static extern int proc_listallpids(int[] buffer, int size);

        [DllImport("/usr/lib/libproc.dylib")]
        private static extern int proc_pidpath(int pid, byte[] buffer, uint size);
    }
}
