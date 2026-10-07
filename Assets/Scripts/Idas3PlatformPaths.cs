using System;
using System.IO;
using UnityEngine;

// Keep writable user data separate from the APK and from read-only packaged
// assets. Windows retains the existing beside-the-player layout; Android/iOS use
// Unity's private persistent directory, which is available without storage
// permissions and survives application updates.
internal static class Idas3PlatformPaths
{
    internal static bool IsAndroid => Application.platform == RuntimePlatform.Android;
    internal static bool IsIOS => Application.platform == RuntimePlatform.IPhonePlayer;
    internal static bool IsMobile => IsAndroid || IsIOS;

    internal static string GameRoot
    {
        get
        {
            if (IsMobile) return Path.Combine(Application.persistentDataPath, "game");
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }
    }

    internal static string RomRoot => Path.Combine(GameRoot, "rom");

    internal static string CustomMusicRoot => IsMobile
        ? Path.Combine(Application.persistentDataPath, "Custom Music")
        : Path.Combine(Path.GetDirectoryName(Application.dataPath), Idas3CustomRaceMusic.FolderName);

    // Set by the Android asset bootstrap once optional imported-course packs
    // have a real filesystem root. Windows keeps its StreamingAssets layout.
    internal static string RuntimeAssetsRoot { get; set; }
    internal static string RuntimePackPath(string pack)
    {
        if (!string.IsNullOrWhiteSpace(RuntimeAssetsRoot)) return Path.Combine(RuntimeAssetsRoot, pack);
        if (IsAndroid) return Path.Combine(Application.persistentDataPath, "RuntimeData", "RuntimeAssets", pack);
        if (IsIOS) return Path.Combine(Application.streamingAssetsPath, "IDAS3", "data", "RuntimeAssets", pack);
        return Path.Combine(Application.streamingAssetsPath, pack);
    }

    internal static string NativePluginFingerprintPath => IsAndroid
        ? Path.Combine(Application.persistentDataPath, "android-native-build-id.txt")
        : IsIOS ? Path.Combine(Application.streamingAssetsPath, "ios-native-build-id.json")
        : Path.Combine(Application.dataPath, "Plugins/x86_64/Idas3Unity.dll");

    // Unity only builds iOS with a numeric version (0.3.95), but online peers,
    // replays and community times compare the full release label. The iOS
    // export bakes that label beside the player; elsewhere Application.version
    // already is the label.
    private static string applicationVersion;
    internal static string ApplicationVersion
    {
        get
        {
            if (applicationVersion != null) return applicationVersion;
            string label = null;
            if (IsIOS)
            {
                string path = Path.Combine(Application.streamingAssetsPath, "idas3-app-version.txt");
                if (File.Exists(path)) label = File.ReadAllText(path).Trim();
            }
            return applicationVersion = string.IsNullOrEmpty(label) ? Application.version : label;
        }
    }
}
