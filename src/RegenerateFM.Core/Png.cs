using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace RegenerateFM
{
    // Minimal PNG support: FM26's IL2CPP interop can't encode PNGs, and the
    // generator must read the API's 8-bit RGB or RGBA output to scale it.
    public static class Png
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

        public sealed class Image
        {
            public int Width, Height;
            public byte[] Rgba;  // top row first
        }

        public static bool IsPng(byte[] data)
        {
            if (data == null || data.Length < Signature.Length)
                return false;
            for (int i = 0; i < Signature.Length; i++)
                if (data[i] != Signature[i])
                    return false;
            return true;
        }

        public static Image Decode(byte[] png)
        {
            if (!IsPng(png))
                throw new InvalidDataException("not a PNG");
            int width = 0, height = 0, colourType = -1;
            var idat = new MemoryStream();
            int offset = 8;
            while (offset + 12 <= png.Length)
            {
                int length = (int)ReadBigEndian(png, offset);
                string type = Encoding.ASCII.GetString(png, offset + 4, 4);
                if (length < 0 || offset + 12 + length > png.Length)
                    throw new InvalidDataException("truncated chunk");
                if (type == "IHDR")
                {
                    width = (int)ReadBigEndian(png, offset + 8);
                    height = (int)ReadBigEndian(png, offset + 12);
                    int depth = png[offset + 16];
                    colourType = png[offset + 17];
                    if (depth != 8 || (colourType != 2 && colourType != 6) || png[offset + 20] != 0)
                        throw new NotSupportedException("only 8-bit, non-interlaced RGB or RGBA PNGs are supported");
                    if (width < 1 || height < 1 || width > 8192 || height > 8192)
                        throw new InvalidDataException("unexpected image size");
                }
                else if (type == "IDAT")
                    idat.Write(png, offset + 8, length);
                else if (type == "IEND")
                    break;
                offset += 12 + length;
            }
            if (colourType < 0)
                throw new InvalidDataException("no image header");
            int channels = colourType == 6 ? 4 : 3;
            int stride = width * channels;
            var raw = new byte[(stride + 1) * height];
            idat.Position = 0;
            using (var zlib = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress))
            {
                int read = 0;
                while (read < raw.Length)
                {
                    int count = zlib.Read(raw, read, raw.Length - read);
                    if (count == 0)
                        throw new InvalidDataException("image data is truncated");
                    read += count;
                }
            }
            var pixels = new byte[stride * height];
            for (int row = 0; row < height; row++)
            {
                int filter = raw[row * (stride + 1)];
                int source = row * (stride + 1) + 1, target = row * stride;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= channels ? pixels[target + i - channels] : 0;
                    int b = row > 0 ? pixels[target - stride + i] : 0;
                    int c = row > 0 && i >= channels ? pixels[target - stride + i - channels] : 0;
                    int x = raw[source + i];
                    switch (filter)
                    {
                        case 0: break;
                        case 1: x += a; break;
                        case 2: x += b; break;
                        case 3: x += (a + b) >> 1; break;
                        case 4:
                            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                            x += pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                            break;
                        default: throw new InvalidDataException("unknown row filter");
                    }
                    pixels[target + i] = (byte)x;
                }
            }
            if (channels == 4)
                return new Image { Width = width, Height = height, Rgba = pixels };
            var rgba = new byte[width * height * 4];
            for (int i = 0, j = 0; i < pixels.Length; i += 3, j += 4)
            {
                rgba[j] = pixels[i];
                rgba[j + 1] = pixels[i + 1];
                rgba[j + 2] = pixels[i + 2];
                rgba[j + 3] = 255;
            }
            return new Image { Width = width, Height = height, Rgba = rgba };
        }

        // Lanczos-3 resampling (as Pillow's LANCZOS) with premultiplied alpha,
        // so transparent pixels don't darken the hair edges.
        public static Image Resize(Image image, int width, int height)
        {
            int count = image.Width * image.Height;
            var premultiplied = new float[count * 4];
            for (int i = 0; i < count; i++)
            {
                float alpha = image.Rgba[i * 4 + 3] / 255f;
                premultiplied[i * 4] = image.Rgba[i * 4] * alpha;
                premultiplied[i * 4 + 1] = image.Rgba[i * 4 + 1] * alpha;
                premultiplied[i * 4 + 2] = image.Rgba[i * 4 + 2] * alpha;
                premultiplied[i * 4 + 3] = image.Rgba[i * 4 + 3];
            }
            var horizontal = ResampleAxis(premultiplied, image.Width, image.Height, width, horizontalAxis: true);
            var both = ResampleAxis(horizontal, width, image.Height, height, horizontalAxis: false);
            var rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                float alpha = Math.Clamp(both[i * 4 + 3], 0f, 255f);
                rgba[i * 4 + 3] = (byte)Math.Round(alpha);
                for (int c = 0; c < 3; c++)
                    rgba[i * 4 + c] = alpha <= 0f ? (byte)0 : (byte)Math.Round(Math.Clamp(both[i * 4 + c] * 255f / alpha, 0f, 255f));
            }
            return new Image { Width = width, Height = height, Rgba = rgba };
        }

        private static float[] ResampleAxis(float[] source, int width, int height, int size, bool horizontalAxis)
        {
            int sourceSize = horizontalAxis ? width : height;
            double scale = (double)sourceSize / size;
            double support = 3.0 * Math.Max(scale, 1.0);
            int outWidth = horizontalAxis ? size : width, outHeight = horizontalAxis ? height : size;
            var result = new float[outWidth * outHeight * 4];
            for (int o = 0; o < size; o++)
            {
                double centre = (o + 0.5) * scale;
                int first = Math.Max(0, (int)Math.Floor(centre - support));
                int last = Math.Min(sourceSize - 1, (int)Math.Ceiling(centre + support));
                var weights = new double[last - first + 1];
                double total = 0;
                for (int s = first; s <= last; s++)
                {
                    double weight = Lanczos((s + 0.5 - centre) / Math.Max(scale, 1.0));
                    weights[s - first] = weight;
                    total += weight;
                }
                int lines = horizontalAxis ? height : width;
                for (int line = 0; line < lines; line++)
                {
                    double r = 0, g = 0, b = 0, a = 0;
                    for (int s = first; s <= last; s++)
                    {
                        double weight = weights[s - first];
                        if (weight == 0)
                            continue;
                        int index = (horizontalAxis ? line * width + s : s * width + line) * 4;
                        r += source[index] * weight;
                        g += source[index + 1] * weight;
                        b += source[index + 2] * weight;
                        a += source[index + 3] * weight;
                    }
                    int target = (horizontalAxis ? line * outWidth + o : o * outWidth + line) * 4;
                    result[target] = (float)(r / total);
                    result[target + 1] = (float)(g / total);
                    result[target + 2] = (float)(b / total);
                    result[target + 3] = (float)(a / total);
                }
            }
            return result;
        }

        private static double Lanczos(double x)
        {
            if (x == 0)
                return 1;
            if (x <= -3 || x >= 3)
                return 0;
            double pi = Math.PI * x;
            return 3 * Math.Sin(pi) * Math.Sin(pi / 3) / (pi * pi);
        }

        // 8-bit RGBA rows as a PNG. Unity's pixel rows are bottom first.
        public static byte[] Encode(byte[] rgba, int width, int height, bool bottomUp)
        {
            int stride = width * 4;
            var raw = new byte[(stride + 1) * height];
            for (int row = 0; row < height; row++)
            {
                int source = (bottomUp ? height - 1 - row : row) * stride, target = row * (stride + 1);
                // The "up" filter compresses portraits better than none.
                raw[target] = (byte)(row == 0 ? 0 : 2);
                for (int i = 0; i < stride; i++)
                {
                    int above = row == 0 ? 0 : rgba[(bottomUp ? height - row : row - 1) * stride + i];
                    raw[target + 1 + i] = (byte)(rgba[source + i] - above);
                }
            }
            byte[] compressed;
            using (var buffer = new MemoryStream())
            {
                using (var zlib = new System.IO.Compression.ZLibStream(buffer, System.IO.Compression.CompressionLevel.Optimal))
                    zlib.Write(raw, 0, raw.Length);
                compressed = buffer.ToArray();
            }
            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8;  // bit depth
            header[9] = 6;  // RGBA
            using (var png = new MemoryStream())
            {
                png.Write(Signature, 0, Signature.Length);
                WriteChunk(png, "IHDR", header);
                WriteChunk(png, "IDAT", compressed);
                WriteChunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var bytes = new byte[12 + data.Length];
            WriteBigEndian(bytes, 0, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, bytes, 4);
            Buffer.BlockCopy(data, 0, bytes, 8, data.Length);
            uint crc = 0xffffffff;
            for (int i = 4; i < 8 + data.Length; i++)
            {
                crc ^= bytes[i];
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ (0xedb88320 & (uint)-(int)(crc & 1));
            }
            WriteBigEndian(bytes, 8 + data.Length, ~crc);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static uint ReadBigEndian(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3]);
        }

        private static void WriteBigEndian(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }
    }
}
