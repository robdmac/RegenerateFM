using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RegenerateFM.Desktop.FM24;
using StbImageSharp;
using Xunit;
using ZstdSharp;

namespace RegenerateFM.Tests
{
    // FM24's archives and the current-portrait lookup Update face uses.
    public class PortraitTests : IDisposable
    {
        private readonly TempFolder _root = new TempFolder();
        private readonly string _game, _graphics, _regeneratefm;

        private static byte[] Text(string value)
        {
            var data = Encoding.UTF8.GetBytes(value);
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)data.Length);
            return length.Concat(data).ToArray();
        }

        private static byte[] Zstd(byte[] data)
        {
            using var compressor = new Compressor();
            return compressor.Wrap(data).ToArray();
        }

        // A minimal .fmf with the validated layout: header, files, then the index,
        // split into frames as large indexes are.
        public static void BuildArchive(string path, Dictionary<string, byte[]> files)
        {
            var magic = new byte[] { 2, 1, (byte)'f', (byte)'m', (byte)'f', (byte)'.', 8, 0, 0 };
            var body = new List<byte>(magic);
            body.AddRange(new byte[8 + 11]);
            var tree = new Node();
            foreach (var (name, data) in files)
            {
                int offset = body.Count;
                var stored = Zstd(data);
                body.AddRange(new byte[GameArchive.LocalHeader]);
                body.AddRange(stored);
                var parts = name.Trim('/').Split('/');
                var node = tree;
                foreach (string folder in parts.Take(parts.Length - 1))
                {
                    if (!node.Folders.TryGetValue(folder, out var child))
                        node.Folders[folder] = child = new Node();
                    node = child;
                }
                string file = parts.Last();
                int dot = file.LastIndexOf('.');
                node.Files.Add((file.Substring(0, dot), file.Substring(dot), offset, stored.Length, data.Length));
            }
            byte[] Directory(string name, Node node)
            {
                var output = new List<byte>(Text(name));
                output.AddRange(BitConverter.GetBytes(node.Files.Count));
                foreach (var (stem, extension, offset, compressed, size) in node.Files)
                {
                    output.AddRange(Text(stem));
                    output.AddRange(Text(extension));
                    foreach (long value in new long[] { offset, compressed, size, 0, 0 })
                        output.AddRange(BitConverter.GetBytes(value));
                }
                output.AddRange(BitConverter.GetBytes(node.Folders.Count));
                foreach (var (folder, child) in node.Folders)
                    output.AddRange(Directory(folder, child));
                return output.ToArray();
            }
            var (rootName, root) = tree.Folders.Single();
            var index = Directory(rootName, root);
            var frames = Enumerable.Range(0, (index.Length + 63) / 64).Select(i => Zstd(index.Skip(64 * i).Take(64).ToArray())).ToList();
            int indexAt = body.Count;
            body.AddRange(new byte[9]);
            body.AddRange(magic);
            foreach (var frame in frames)
            {
                body.AddRange(BitConverter.GetBytes(frame.Length));
                body.AddRange(frame);
            }
            var bytes = body.ToArray();
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(9), indexAt);
            File.WriteAllBytes(path, bytes);
        }

        private sealed class Node
        {
            public readonly Dictionary<string, Node> Folders = new Dictionary<string, Node>();
            public readonly List<(string, string, int, int, int)> Files = new List<(string, string, int, int, int)>();
        }

        // An 8×8 image: RGB (a JPEG-like photo, stored as PNG) or a grey mask.
        private static byte[] Image(byte r, byte g, byte b, byte alpha = 255, bool[,] opaque = null)
        {
            var rgba = new byte[8 * 8 * 4];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int i = (y * 8 + x) * 4;
                    rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b;
                    rgba[i + 3] = opaque != null ? (opaque[x, y] ? (byte)255 : (byte)0) : alpha;
                }
            return Png.Encode(rgba, 8, 8, bottomUp: false);
        }

        // A baseline JPEG of one colour, as FM24's bundled photos are (made with Pillow).
        private static readonly byte[] Jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "photo.jpg"));

        public PortraitTests()
        {
            _game = _root.Combine("Football Manager 2024");
            Directory.CreateDirectory(Path.Combine(_game, "data", "game"));
            _graphics = _root.Combine("graphics");
            _regeneratefm = Path.Combine(_graphics, "regeneratefm");
            Directory.CreateDirectory(_regeneratefm);
            string club = "/graphics/pictures/players/normal/usa/inter miami/";
            BuildArchive(Path.Combine(_game, "data", "game", "graphics.fmf"), new Dictionary<string, byte[]>
            {
                [club + "config.xml"] = Encoding.ASCII.GetBytes("<record><list id=\"maps\"><record from=\"lionel messi\" to=\"graphics/pictures/person/7458500/portrait\"/></list></record>"),
                [club + "lionel messi.jpg"] = Jpeg,
                // A greyscale mask ("L"), as FM24's are, made with Pillow.
                [club + "lionel messi_[mask].png"] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mask.png")),
            });
            ExistingPortrait.Forget();
        }

        public void Dispose() => _root.Dispose();

        [Fact]
        public void FilesAreReadFromAnArchive()
        {
            string archive = Path.Combine(_game, "data", "game", "graphics.fmf");
            var entries = GameArchive.ReadIndex(archive);
            var config = entries["/graphics/pictures/players/normal/usa/inter miami/config.xml"];
            Assert.Contains("lionel messi", Encoding.ASCII.GetString(GameArchive.ReadFile(archive, config)));
        }

        [Fact]
        public void ATruncatedPhotoIsRefusedBeforeAnyRequest()
        {
            byte[] mask = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mask.png"));
            Assert.True(ExistingPortrait.Complete(Jpeg) && ExistingPortrait.Complete(mask));
            // Cut in the scan data, or in the first segment (where stb_image would loop).
            foreach (int length in new[] { Jpeg.Length - 2, Jpeg.Length / 2, 12, 2 })
                Assert.Contains("truncated", Assert.Throws<InvalidDataException>(() => ExistingPortrait.RgbaPng(Jpeg[..length], null)).Message);
            Assert.Throws<InvalidDataException>(() => ExistingPortrait.RgbaPng(Jpeg, mask[..(mask.Length - 12)]));
            Assert.NotNull(ExistingPortrait.RgbaPng(Jpeg.Concat(new byte[] { 0, 0 }).ToArray(), mask));
        }

        [Fact]
        public void ABundledPhotoBecomesATransparentPng()
        {
            var (png, source) = ExistingPortrait.Find("7458500", _regeneratefm, _game).Value;
            Assert.Contains("bundled", source);
            var picture = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);
            Assert.Equal(ColorComponents.RedGreenBlueAlpha, picture.SourceComp);
            Assert.Equal(0, picture.Data[3]);                    // (0, 0) is outside the mask
            Assert.Equal(255, picture.Data[(4 * 8 + 4) * 4 + 3]);  // (4, 4) is inside
        }

        [Fact]
        public void TheUsersGraphicsReplaceBundledOnesAndRegenerateFMsComeFirst()
        {
            string facepack = Path.Combine(_graphics, "facepack");
            Directory.CreateDirectory(facepack);
            File.WriteAllText(Path.Combine(facepack, "config.xml"),
                              "<record><list id=\"maps\"><record from=\"messi\" to=\"graphics/pictures/person/7458500/portrait\"/></list></record>");
            File.WriteAllBytes(Path.Combine(facepack, "messi.png"), Image(0, 0, 255));
            Assert.Equal(Path.Combine(facepack, "messi.png"), ExistingPortrait.Find("7458500", _regeneratefm, _game).Value.Source);
            File.WriteAllText(Path.Combine(_regeneratefm, "config.xml"),
                              "<record><list id=\"maps\"><record from=\"7458500-0123456789abcdef\" to=\"graphics/pictures/person/r-7458500/portrait\"/></list></record>");
            File.WriteAllBytes(Path.Combine(_regeneratefm, "7458500-0123456789abcdef.png"), Image(0, 255, 0));
            Assert.Equal(Path.Combine(_regeneratefm, "7458500-0123456789abcdef.png"), ExistingPortrait.Find("7458500", _regeneratefm, _game).Value.Source);
        }

        [Fact]
        public void APersonWithoutAPictureHasNoPortrait()
        {
            Assert.Null(ExistingPortrait.Find("123", _regeneratefm, _game));
            Assert.Null(ExistingPortrait.Find("7458500", _regeneratefm, null));
            Assert.Throws<ArgumentException>(() => ExistingPortrait.Find("../7458500", _regeneratefm, _game));
        }

        [Fact]
        public void TheFaceCheckMatchesTheLookupWithoutDecoding()
        {
            // The DLL greys out Update face on the portrait popup and Overview menu when this says no.
            double now = 0;
            ExistingPortrait.Clock = () => now;
            try
            {
                bool Check(string person) => ExistingPortrait.Has(person, _regeneratefm, _game);
                Assert.True(Check("7458500"));
                Assert.False(Check("123"));
                Assert.False(ExistingPortrait.Has("7458500", _regeneratefm, null));
                // RegenerateFM's own folder is read again as soon as its config changes.
                File.WriteAllText(Path.Combine(_regeneratefm, "config.xml"),
                                  "<record><list id=\"maps\"><record from=\"123-0123456789abcdef\" to=\"graphics/pictures/person/r-123/portrait\"/></list></record>");
                Assert.False(Check("123"));  // Mapped, but the picture is missing.
                File.WriteAllBytes(Path.Combine(_regeneratefm, "123-0123456789abcdef.png"), Image(0, 255, 0));
                Assert.True(Check("123"));
                Assert.NotNull(ExistingPortrait.Find("123", _regeneratefm, null));
                // Another folder's config is found by the next walk.
                string facepack = Path.Combine(_graphics, "facepack");
                Directory.CreateDirectory(facepack);
                File.WriteAllText(Path.Combine(facepack, "config.xml"),
                                  "<record><list id=\"maps\"><record from=\"kim\" to=\"graphics/pictures/person/456/portrait\"/></list></record>");
                File.WriteAllBytes(Path.Combine(facepack, "kim.jpg"), Jpeg);
                Assert.False(Check("456"));
                now += ExistingPortrait.ConfigRescan + 1;
                Assert.True(Check("456"));
                Assert.Throws<ArgumentException>(() => Check("../456"));
            }
            finally
            {
                ExistingPortrait.Clock = () => Environment.TickCount64 / 1000.0;
            }
        }

        [Fact]
        public void TheFaceCheckDoesNotWaitForTheArchiveIndex()
        {
            Assert.Throws<ExistingPortrait.StillIndexingException>(() => ExistingPortrait.Has("7458500", _regeneratefm, _game, wait: false));
            bool ready = false;
            for (int i = 0; i < 100 && !ready; i++)
            {
                Thread.Sleep(50);
                try
                {
                    ready = ExistingPortrait.Has("7458500", _regeneratefm, _game, wait: false);
                }
                catch (ExistingPortrait.StillIndexingException)
                {
                }
            }
            Assert.True(ready);
        }
    }
}
