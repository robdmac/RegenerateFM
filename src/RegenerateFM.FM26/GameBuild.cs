using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using SI.Bindable;
using SI.Core;
using SI.Input;
using SI.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace RegenerateFM.FM26
{
    // The FM26 builds RegenerateFM was checked against, by their binaries' hashes; on any
    // other build the plugin turns itself off. FM27 will need its own.
    public sealed partial class Plugin
    {
        private bool SupportedGameBuild()
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    string contents = Path.Combine(Paths.GameRootPath, "fm.app", "Contents");
                    string macAssembly = Path.Combine(contents, "Frameworks", "GameAssembly.dylib");
                    string macPlugin = Path.Combine(contents, "PlugIns", "game_plugin.bundle", "Contents", "MacOS", "game_plugin");
                    if (HasHash(macAssembly, "7886b66692c18504de32ad3973fe4c9afc3edfe12a6941b44e83627cb9563e26")
                        && HasHash(macPlugin, "de6b048ab1a95fd4ae683a7d751a33645183a9a570fa1c2fba32fd308ca87979"))
                        return true;
                    // The FM26 Demo for Mac, 26.3.2 too, with its own user data.
                    if (HasHash(macAssembly, "b3f86da82f7ef5b24d5a0cae4d4cccd4f904d0e8b971d3e7b92c78500a14eda5")
                        && HasHash(macPlugin, "ef6fb6078a5dfceaa325799caf00bb635f3a7dbd8cc4aa3021aa7beeccf8cc4d"))
                    {
                        UserDataName = "Football Manager 26 Demo";
                        return true;
                    }
                    return false;
                }
                string gameAssembly = Path.Combine(Paths.GameRootPath, "GameAssembly.dll");
                string gamePlugin = Path.Combine(Paths.GameRootPath, "fm_Data", "Plugins", "x86_64", "game_plugin.dll");
                if (HasHash(gameAssembly, "7ce3eb474dc6093df633f979e869e55b2ec7953fde2e732392a694d379ff7a0c")
                    && HasHash(gamePlugin, "eb6c86fab56e051fe41482c7b93d8cb0af716a1cc2c6197ce1babbff68372bb8"))
                    return true;
                // The FM26 Demo (Steam app 3551360) is a separate 26.3.2 build
                // that keeps its user data in "Football Manager 26 Demo".
                if (HasHash(gameAssembly, "edcd74617b881817da1895f4272add8fcfcab9d15a4ab64d858d408f59c730f0")
                    && HasHash(gamePlugin, "a4181cf61a265058d0b447fd0d679d5ac282b08a7962f0f0d67dbd27bdf9aa26"))
                {
                    UserDataName = "Football Manager 26 Demo";
                    return true;
                }
                return false;
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not verify the FM26 build: " + error);
                return false;
            }
        }

        private static bool HasHash(string path, string expected)
        {
            if (!File.Exists(path))
                return false;
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return string.Equals(Convert.ToHexString(sha.ComputeHash(stream)), expected,
                                     StringComparison.OrdinalIgnoreCase);
        }
    }
}
