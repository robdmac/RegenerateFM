using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Doorstop
{
    // Doorstop's entry point for FM26, before BepInEx: the release's
    // doorstop_config.ini names this assembly on Windows (and Proton), and
    // run_regeneratefm.sh on the Mac. It sits beside BepInEx's in BepInEx/core.
    //
    // BepInEx keeps its log in memory until it has prepared FM26, so to show
    // progress this copies BepInEx's log lines as they happen to
    // BepInEx/RegenerateFM.progress.log, which regeneratefm-progress reads. On
    // Windows it also starts regeneratefm-progress.exe (on the Mac the
    // launcher does). The window shows only while BepInEx prepares FM26 (its
    // first start, and its first after an FM26 update). Then it starts BepInEx
    // as Doorstop would have. All of this is optional: if any of it fails,
    // BepInEx still starts.
    public static class Entrypoint
    {
        public const string ProgressLog = "RegenerateFM.progress.log";

        public static void Start()
        {
            string core = Path.GetDirectoryName(typeof(Entrypoint).Assembly.Location);
            if (string.IsNullOrEmpty(core))
                core = Path.GetDirectoryName(Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH"));
            string folder = Path.GetDirectoryName(core);
            try
            {
                CopyLog(core, Path.Combine(folder, ProgressLog));
                if (OperatingSystem.IsWindows())
                    StartProgress(core, folder);
            }
            catch (Exception)
            {
            }
            // BepInEx finds its folders from the assembly Doorstop started.
            string bepinex = Path.Combine(core, "BepInEx.Unity.IL2CPP.dll");
            Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", bepinex);
            var start = Assembly.LoadFrom(bepinex).GetType("Doorstop.Entrypoint", true)
                                .GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
            try
            {
                start.Invoke(null, null);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            }
        }

        // Adds a listener (Info and above) to BepInEx's Logger that writes each
        // line to ``path`` at once. It is made at run time (DispatchProxy) for
        // BepInEx's ILogListener, so this works with any BepInEx 6 build.
        private static void CopyLog(string core, string path)
        {
            var bepinex = Assembly.LoadFrom(Path.Combine(core, "BepInEx.Core.dll"));
            var listenerType = bepinex.GetType("BepInEx.Logging.ILogListener", true);
            var listeners = (IEnumerable)bepinex.GetType("BepInEx.Logging.Logger", true).GetProperty("Listeners").GetValue(null);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            var listener = typeof(DispatchProxy).GetMethod(nameof(DispatchProxy.Create))
                                                .MakeGenericMethod(listenerType, typeof(LogCopy)).Invoke(null, null);
            ((LogCopy)listener).Writer = writer;
            listeners.GetType().GetMethods().First(method => method.Name == "Add" && method.GetParameters().Length == 1)
                     .Invoke(listeners, new[] { listener });
        }

        // regeneratefm-progress.exe for this FM26: the process to wait for, the
        // log to read, and its user-data folder's name, where its language is.
        private static void StartProgress(string core, string folder)
        {
            string progress = Path.Combine(core, "regeneratefm-progress.exe");
            if (!File.Exists(progress))
                return;
            string game = Path.GetFileName(Path.GetDirectoryName(folder)) ?? "";
            var info = new ProcessStartInfo(progress) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--pid");
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--log");
            info.ArgumentList.Add(Path.Combine(folder, ProgressLog));
            info.ArgumentList.Add("--name");
            info.ArgumentList.Add(game.IndexOf("Demo", StringComparison.OrdinalIgnoreCase) >= 0 ? "Football Manager 26 Demo" : "Football Manager 26");
            Process.Start(info)?.Dispose();
        }
    }

    // BepInEx's ILogListener for Info and above (Cpp2IL's Debug lines are many,
    // and the progress window needs none): each event's line goes to Writer.
    public class LogCopy : DispatchProxy
    {
        public StreamWriter Writer;

        protected override object Invoke(MethodInfo method, object[] args)
        {
            switch (method.Name)
            {
                case "get_LogLevelFilter":
                    return Enum.Parse(method.ReturnType, "Fatal, Error, Warning, Message, Info");
                case "LogEvent":
                    try
                    {
                        lock (this)
                            Writer?.WriteLine(args[1]?.ToString());
                    }
                    catch (Exception)
                    {
                        Writer = null;
                    }
                    return null;
                case "Dispose":
                    lock (this)
                    {
                        Writer?.Dispose();
                        Writer = null;
                    }
                    return null;
            }
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }
}
