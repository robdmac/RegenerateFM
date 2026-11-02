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
    // The builds RegenerateFM was checked against, by their binaries' hashes.
    // Any other build of FM26 or later runs too, named by Unity's app.info
    // (GameInfo): the patches it needs are applied one by one (Plugin.Load),
    // so a game update that changes what RegenerateFM uses turns off only
    // that part, and the log says which.
    public sealed partial class Plugin
    {
        // FM27 or later rather than FM26, whose few changed APIs Profiles.cs calls by name.
        internal static bool Fm27;
        // A build in the list below, rather than one named by app.info.
        internal static bool TestedBuild;

        // Sets Fm27, the game's name in RegenerateFM's text and its user data
        // folder; false for anything that isn't FM26 or later.
        private bool IdentifyGame()
        {
            try
            {
                if (TestedGameBuild())
                {
                    TestedBuild = true;
                    return true;
                }
                string appInfo = OperatingSystem.IsMacOS()
                    ? Path.Combine(Paths.GameRootPath, "fm.app", "Contents", "Resources", "Data", "app.info")
                    : Path.Combine(Paths.GameRootPath, "fm_Data", "app.info");
                var game = File.Exists(appInfo) ? RegenerateFM.GameInfo.FromAppInfo(File.ReadAllText(appInfo)) : null;
                if (game == null)
                {
                    Log.LogWarning("RegenerateFM couldn't tell which Football Manager this is (" + appInfo + "); buttons disabled");
                    return false;
                }
                Fm27 = game.Number >= 27;
                Text.GameName = game.ShortName;
                UserDataName = game.UserDataName;
                Log.LogWarning("RegenerateFM hasn't been checked on this " + game.UserDataName + " build yet; it runs, " +
                               "and turns off anything a game update changed");
                return true;
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not tell which Football Manager this is: " + error);
                return false;
            }
        }

        private bool TestedGameBuild()
        {
            if (OperatingSystem.IsMacOS())
            {
                string contents = Path.Combine(Paths.GameRootPath, "fm.app", "Contents");
                string macAssembly = Path.Combine(contents, "Frameworks", "GameAssembly.dylib");
                string macPlugin = Path.Combine(contents, "PlugIns", "game_plugin.bundle", "Contents", "MacOS", "game_plugin");
                if (HasHash(macAssembly, "7886b66692c18504de32ad3973fe4c9afc3edfe12a6941b44e83627cb9563e26")
                    && HasHash(macPlugin, "de6b048ab1a95fd4ae683a7d751a33645183a9a570fa1c2fba32fd308ca87979"))
                    return true;
                // FM27 27.0.0 (Steam), with its own user data: the first build
                // and Steam's update of 2 November 2026 (build 26340721).
                if (HasHash(macAssembly, "266d02a57471b44f381b7b647828c58fd394e8222c3083613324a93a9db09dd7")
                    && HasHash(macPlugin, "2e915b0f264920ae1205b111f45d50e354ac0e7d0b6e136b99189c24b265f431")
                    || HasHash(macAssembly, "d26f882851f26248bb56ca3575fc7759d74aebc21fb9722f44cab29b63c1e654")
                    && HasHash(macPlugin, "774cacde63567fd3901c47719e7a6ed94cc3a0a62e9b94989dcac6b4890ec0e1"))
                {
                    Fm27 = true;
                    Text.GameName = "FM27";
                    UserDataName = "Football Manager 27";
                    return true;
                }
                // Epic's FM26 for Mac: the same 26.3.2 code built again, so
                // only its build IDs, build times and signatures differ.
                if (HasHash(macAssembly, "a34a3cd631653867136eb8c7728242d73a64735f77dcc24b3bcf1f541d23d2e2")
                    && HasHash(macPlugin, "e98ab747b32746c45e7bc1e2fc186eea9ace818c52b2519a5266539ee4814eed"))
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
            // FM27 27.0.0 for Windows (Steam), with its own user data. Epic's FM27
            // Launch Preview has the same GameAssembly.dll and a game_plugin.dll
            // packed differently, as Epic's FM26 does. Epic's update to
            // 27.0.0+2493426 rebuilt both with the same metadata.
            if (HasHash(gameAssembly, "820d6618acafac7dffc1ef3b489582eddb84b0343d4f34d55fff9fb70881e00a")
                    && (HasHash(gamePlugin, "bc940e472418fc5c214208d7cf30fecf600f689b6f9d71c11ba19a2df0fe057f")
                        || HasHash(gamePlugin, "55725511227555e3950cd2a58dd86d9f70969cf6d7566140fa1a4af560485ba8"))
                || HasHash(gameAssembly, "4972a1a9ee485e9283bef073145282719e1730a23f104156bd5d0d64c089ea26")
                    && HasHash(gamePlugin, "36e6027b63605a227ce583452bf90b313adc48e819f74bbeb2bae1ed52c5a2ac"))
            {
                Fm27 = true;
                Text.GameName = "FM27";
                UserDataName = "Football Manager 27";
                return true;
            }
            // Epic's FM26 for Windows: GameAssembly.dll is the same code built
            // again (only its build time and debug ID differ) with the same
            // metadata; its game_plugin.dll, whose code the plugin doesn't
            // use, is packed differently.
            if (HasHash(gameAssembly, "1f2dbcce6deb7ca40eb1309d10c818e1217fe9e43c4c674d0fea90f1069f5982")
                && HasHash(gamePlugin, "d86f511cec5a8de871ceedebf3eab05cec057bf2f82ee9ee0ef2e923e1a60944"))
                return true;
            // The FM26 Demo (Steam app 3551360) is a separate 26.3.2 build
            // that keeps its user data in "Football Manager 26 Demo".
            // Epic's Demo has the same GameAssembly.dll, and a game_plugin.dll
            // packed differently, as Epic's full game does.
            if (HasHash(gameAssembly, "edcd74617b881817da1895f4272add8fcfcab9d15a4ab64d858d408f59c730f0")
                && (HasHash(gamePlugin, "a4181cf61a265058d0b447fd0d679d5ac282b08a7962f0f0d67dbd27bdf9aa26")
                    || HasHash(gamePlugin, "26f292cf4b0af33a41f075d086537f3aadb3c14cbcee0e5206f2270e83809cad")))
            {
                UserDataName = "Football Manager 26 Demo";
                return true;
            }
            return false;
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
