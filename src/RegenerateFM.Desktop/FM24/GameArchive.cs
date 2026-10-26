using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ZstdSharp;

namespace RegenerateFM.Desktop.FM24
{
    // Files in FM24's .fmf game archives (read-only). In the validated 24.4.2
    // archives, byte 9 holds the offset of a zstd-compressed index near the end
    // of the file. The index is a directory tree; each file entry gives the
    // offset of a 30-byte local header followed by the file's data,
    // zstd-compressed or stored as is.
    public static class GameArchive
    {
        public sealed class Entry
        {
            public string Path;
            public long Offset;
            public long Compressed;
            public long Size;
        }

        private static readonly byte[] ZstdMagic = { 0x28, 0xB5, 0x2F, 0xFD };
        public const int LocalHeader = 30;
        public const int MaxIndex = 64 << 20;
        public const int MaxFile = 32 << 20;

        private static bool StartsWithMagic(ReadOnlySpan<byte> data) => data.Length >= 4 && data.Slice(0, 4).SequenceEqual(ZstdMagic);

        // Large data is stored as frames of up to 128 KiB each, every frame after
        // the first preceded by its 32-bit compressed length.
        private static unsafe byte[] Decompress(ReadOnlySpan<byte> data, long limit)
        {
            var output = new MemoryStream();
            using var decompressor = new Decompressor();
            while (output.Length <= limit)
            {
                if (output.Length > 0 && data.Length >= 8 && data.Slice(4, 4).SequenceEqual(ZstdMagic))
                    data = data.Slice(4);
                if (!StartsWithMagic(data))
                    break;
                ulong frame;
                fixed (byte* start = data)
                    frame = ZstdSharp.Unsafe.Methods.ZSTD_findFrameCompressedSize(start, (nuint)data.Length);
                if (ZstdSharp.Unsafe.Methods.ZSTD_isError((nuint)frame) || frame > (ulong)data.Length)
                    throw new InvalidDataException("FM24 archive data is truncated.");
                try
                {
                    output.Write(decompressor.Unwrap(data.Slice(0, (int)frame)));
                }
                catch (ZstdException error)
                {
                    throw new InvalidDataException("FM24 archive data could not be read: " + error.Message);
                }
                data = data.Slice((int)frame);
            }
            if (output.Length > limit)
                throw new InvalidDataException("FM24 archive data is larger than expected.");
            return output.ToArray();
        }

        // Every file in the archive by lower-case path ("/graphics/...").
        public static Dictionary<string, Entry> ReadIndex(string archive)
        {
            byte[] tail;
            using (var file = File.OpenRead(archive))
            {
                var header = new byte[17];
                if (file.Read(header, 0, 17) != 17 || Encoding.ASCII.GetString(header, 2, 4) != "fmf.")
                    throw new InvalidDataException("Not an FM archive: " + archive);
                file.Seek(BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(9)), SeekOrigin.Begin);
                tail = new byte[Math.Min(MaxIndex, Math.Max(0, file.Length - file.Position))];
                int read = 0;
                while (read < tail.Length)
                {
                    int count = file.Read(tail, read, tail.Length - read);
                    if (count == 0)
                        break;
                    read += count;
                }
                Array.Resize(ref tail, read);
            }
            int start = tail.AsSpan(0, Math.Min(64, tail.Length)).IndexOf(ZstdMagic);
            if (start < 0)
                throw new InvalidDataException("FM archive index was not found: " + archive);
            var data = Decompress(tail.AsSpan(start), MaxIndex);
            var entries = new Dictionary<string, Entry>();
            int position = 0;
            uint U32()
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
                position += 4;
                return value;
            }
            string Text()
            {
                uint length = U32();
                if (length > 4096)
                    throw new InvalidDataException("FM archive index has an invalid name.");
                string value = Memory.People.Utf8.GetString(data, position, (int)length);
                position += (int)length;
                return value;
            }
            void Directory(string parent, int depth)
            {
                if (depth > 64)
                    throw new InvalidDataException("FM archive index is nested too deeply.");
                string path = parent + "/" + Text();
                uint files = U32();
                for (uint i = 0; i < files; i++)
                {
                    string name = Text() + Text();
                    var entry = new Entry
                    {
                        Path = path + "/" + name,
                        Offset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position)),
                        Compressed = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position + 8)),
                        Size = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position + 16)),
                    };
                    position += 40;
                    entries[entry.Path.ToLowerInvariant()] = entry;
                }
                uint folders = U32();
                for (uint i = 0; i < folders; i++)
                    Directory(path, depth + 1);
            }
            try
            {
                Directory("", 0);
            }
            catch (Exception error) when (error is ArgumentOutOfRangeException || error is ArgumentException)
            {
                throw new InvalidDataException("FM archive index could not be read: " + archive);
            }
            return entries;
        }

        public static byte[] ReadFile(string archive, Entry entry)
        {
            if (entry.Size > MaxFile || entry.Compressed > MaxFile || entry.Size < 0 || entry.Compressed < 0)
                throw new InvalidDataException(entry.Path + " is too large to read.");
            var data = new byte[entry.Compressed];
            using (var file = File.OpenRead(archive))
            {
                file.Seek(entry.Offset + LocalHeader, SeekOrigin.Begin);
                int read = 0;
                while (read < data.Length)
                {
                    int count = file.Read(data, read, data.Length - read);
                    if (count == 0)
                        throw new InvalidDataException(entry.Path + " is truncated in the archive.");
                    read += count;
                }
            }
            if (StartsWithMagic(data))
                data = Decompress(data, entry.Size);
            if (data.Length != entry.Size)
                throw new InvalidDataException(entry.Path + " has an unexpected size in the archive.");
            return data;
        }
    }
}
