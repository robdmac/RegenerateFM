using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RegenerateFM
{
    // JSON written field by field, without reflection, so the trimmed desktop
    // programs and the plugin write it the same way.
    public static class Json
    {
        public static byte[] Write(Action<Utf8JsonWriter> write, bool indented = false)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
                   {
                       Indented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
                write(writer);
            return buffer.ToArray();
        }

        public static string Text(Action<Utf8JsonWriter> write, bool indented = false) => Encoding.UTF8.GetString(Write(write, indented));
    }
}
