using System.IO;
using System.Reflection;
using System.Text;

namespace RegenerateFM.Desktop
{
    // Files built into the program (RegenerateFM.Desktop.csproj): FM24's native
    // library for this system, the Mac launcher and the checked builds' data.
    public static class Resources
    {
        // The file's bytes, or null if this build doesn't carry it.
        public static byte[] Bytes(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null)
                return null;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        public static string Text(string name)
        {
            var data = Bytes(name) ?? throw new FileNotFoundException("This RegenerateFM build doesn't carry " + name + ".");
            return new UTF8Encoding(false).GetString(data);
        }
    }
}
