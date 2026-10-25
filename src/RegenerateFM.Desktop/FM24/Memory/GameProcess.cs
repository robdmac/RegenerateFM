using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace RegenerateFM.Desktop.FM24.Memory
{
    // The running FM24 or a supported person record was unavailable; the
    // message says why in the player's terms.
    public class MemoryUnavailableException : Exception
    {
        public MemoryUnavailableException(string message) : base(message) { }
    }

    // Read-only access to the running FM24: ReadProcessMemory on Windows (and
    // so under Proton), process_vm_readv on Linux, and RegenerateFM's library
    // inside the game on macOS, which macOS requires. Nothing here attaches
    // to, pauses or writes to the game.
    public abstract class GameProcess : IDisposable
    {
        public const int MaxRead = 4096;

        protected GameProcess(int pid, Layout layout)
        {
            Pid = pid;
            Layout = layout;
        }

        public int Pid { get; }
        public Layout Layout { get; }

        // The address of FM24's image header; InvalidDataException if this
        // process isn't FM24 or can't be read.
        public abstract long ImageBase { get; }

        // The game's executable (fm.exe, or fm.app/Contents/MacOS/fm) as this
        // system names it, or null.
        public abstract string Executable { get; }

        // The regions where FM24 keeps its live objects: committed, private,
        // read/write memory.
        public abstract IEnumerable<(long Start, long End)> WritableRegions();

        // Reads up to buffer.Length bytes; how many were read. IOException when
        // none could be.
        public abstract int ReadInto(long address, Span<byte> buffer);

        // How much a scan reads at a time.
        public virtual int ScanChunk => 2 << 20;

        // What a scan reads (ReadInto, unless a test's fake memory fills its gaps).
        public virtual int ScanInto(long address, Span<byte> buffer) => ReadInto(address, buffer);

        // Exactly ``size`` bytes (1 to 4096) from a known address.
        public byte[] Read(long address, int size)
        {
            if (address <= 0 || size < 1 || size > MaxRead)
                throw new ArgumentException("Invalid process address or read size.");
            var data = new byte[size];
            int read = ReadInto(address, data);
            if (read != size)
                throw new IOException("Partial process read: expected " + size + " bytes, got " + read + ".");
            return data;
        }

        public ushort U16(long address) => BinaryPrimitives.ReadUInt16LittleEndian(Read(address, 2));
        public uint U32(long address) => BinaryPrimitives.ReadUInt32LittleEndian(Read(address, 4));
        public int I32(long address) => BinaryPrimitives.ReadInt32LittleEndian(Read(address, 4));
        public long U64(long address) => BinaryPrimitives.ReadInt64LittleEndian(Read(address, 8));

        // A pointer stored in a person, nation, club or contract object: the
        // layout's pointer size (4 bytes under Wine, 8 on the Mac).
        public long Pointer(long address) => Layout.Pointer == 4 ? U32(address) : U64(address);

        public MemoryScan Scan(int overlap) => new MemoryScan(this, overlap);

        // Caches kept while this game runs (see ProfileReader, PersonHandles, SquadReader).
        internal readonly object CacheLock = new object();
        internal readonly Dictionary<string, (long Address, PersonIdentity Identity, byte[] Header)> Records =
            new Dictionary<string, (long, PersonIdentity, byte[])>();
        internal int? PersonTableSlot;
        internal long? PersonTable;

        public virtual void Dispose()
        {
        }
    }

    // One piece of a scan: Buffer[0..Length] is the game's memory from Address.
    // It is valid only until the scan moves on.
    public sealed class ScanChunk
    {
        public long Address;
        public byte[] Buffer;
        public int Length;
        public ReadOnlySpan<byte> Span => new ReadOnlySpan<byte>(Buffer, 0, Length);
    }

    // FM24's writable memory in overlapping chunks: each chunk starts with the
    // previous one's last ``overlap`` bytes, so a needle across a boundary is
    // still found. Complete becomes false if the game freed or shrank a region
    // during the scan; callers that infer absence must check it.
    public sealed class MemoryScan : IEnumerable<ScanChunk>
    {
        private readonly GameProcess _game;
        private readonly int _overlap;

        public MemoryScan(GameProcess game, int overlap)
        {
            _game = game;
            _overlap = overlap;
        }

        public bool Complete { get; private set; } = true;
        public bool Scanned { get; private set; }

        public IEnumerator<ScanChunk> GetEnumerator()
        {
            int size = _game.ScanChunk;
            var chunk = new ScanChunk { Buffer = new byte[_overlap + size] };
            foreach (var (start, end) in new List<(long, long)>(_game.WritableRegions()))
            {
                long address = start;
                int carried = 0;
                while (address < end)
                {
                    int wanted = (int)Math.Min(size, end - address);
                    int read;
                    try
                    {
                        read = _game.ScanInto(address, new Span<byte>(chunk.Buffer, carried, wanted));
                    }
                    catch (IOException)
                    {
                        Complete = false;
                        break;  // The game freed this region during the scan.
                    }
                    if (read != wanted)
                    {
                        Complete = false;
                        if (read == 0)
                            break;
                    }
                    Scanned = true;
                    chunk.Address = address - carried;
                    chunk.Length = carried + read;
                    yield return chunk;
                    int keep = Math.Min(_overlap, chunk.Length);
                    Array.Copy(chunk.Buffer, chunk.Length - keep, chunk.Buffer, 0, keep);
                    carried = keep;
                    address += read;
                }
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
