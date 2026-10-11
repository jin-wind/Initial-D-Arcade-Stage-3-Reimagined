using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

// iPhoneOS-only EOS input and Xcode wiring. Keep this opt-in and separate from
// the established Simulator/offline/LAN exporter. No credentials are read here.
internal static class Idas3EosIOSBuild
{
    internal const string Define = "IDAS3_EOS_IOS";
    internal const string Framework = "Assets/Plugins/iOS/EOSSDK.framework";
    const string Manifest = "Assets/Plugins/iOS/Idas3EosBuild.json";
    const string BinarySha256 = "702f3e930258f06b7d55a74c91d9750f8b325fa2c2c3a656f6bcb7c2c3bc152b";
    internal static bool EnabledForExport { get; set; }
    [Serializable] sealed class SdkManifest
    {
        public int schema;
        public string sdk, minimumOS, pluginVersion, eosVersion, archiveSha256, binarySha256;
        public string[] architectures;
    }

    internal static bool IsFramework(string path) =>
        path == Framework || path.StartsWith(Framework + "/", StringComparison.Ordinal);

    internal static void Validate(bool simulator, bool enabled)
    {
        if (simulator && enabled)
            throw new InvalidOperationException("EOS is device-only. Export the ordinary Simulator build for offline/LAN tests.");
        // The exporter owns this per-build symbol. A global symbol could make
        // an ordinary Simulator export reference a device-only native library.
        string symbols = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.iOS);
        string response = File.Exists("Assets/csc.rsp") ? File.ReadAllText("Assets/csc.rsp") : "";
        if (Array.IndexOf(symbols.Split(';'), Define) >= 0 || response.Contains(Define))
            throw new InvalidOperationException("Remove global " + Define + "; use BuildDeviceEosChecked to enable EOS for one device export.");
        if (!enabled) return;
        if (!File.Exists(Manifest) || !File.Exists(Framework + "/EOSSDK") ||
            !File.Exists("Assets/EOS_SDK/Generated/Platform/InitializeOptions.cs") ||
            !File.Exists("Assets/EOS_SDK/license.txt") || !File.Exists("Assets/EOS_SDK/ThirdPartySoftwareNotice.txt"))
            throw new FileNotFoundException("Run python3 Tools/prepare-eos-ios.py before an EOS device export.");
        var manifest = JsonUtility.FromJson<SdkManifest>(File.ReadAllText(Manifest));
        if (manifest == null || manifest.schema != 1 || manifest.sdk != "iphoneos" ||
            manifest.architectures == null || manifest.architectures.Length != 1 || manifest.architectures[0] != "arm64" ||
            manifest.minimumOS != "15.0" || manifest.pluginVersion != "6.2.0" || manifest.eosVersion != "1.19.2.1" ||
            manifest.binarySha256 != BinarySha256 ||
            manifest.archiveSha256 != "aafe5a1cc278f2f65e0373777706d0a1028eea49b9548ef4a7520cc50f647c4b")
            throw new InvalidOperationException("EOS iPhoneOS SDK manifest mismatch. Re-run Tools/prepare-eos-ios.py.");
        using (var stream = File.OpenRead(Framework + "/EOSSDK"))
        using (var hash = SHA256.Create())
            if (BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() != BinarySha256)
                throw new InvalidOperationException("EOS iPhoneOS framework checksum mismatch. Re-stage the verified SDK.");
    }

    // We wire this dynamic framework in postprocess rather than allowing
    // Unity to auto-copy it. This gives one known embed location and prevents
    // device-framework leakage into a later Simulator or ordinary export.
    internal static void ExcludeAutomaticImport(PluginImporter plugin)
    {
        plugin.SetCompatibleWithAnyPlatform(false);
        plugin.SetCompatibleWithEditor(false);
        foreach (BuildTarget target in new[] { BuildTarget.iOS, BuildTarget.Android, BuildTarget.StandaloneOSX,
            BuildTarget.StandaloneWindows64, BuildTarget.StandaloneLinux64 })
            plugin.SetCompatibleWithPlatform(target, false);
        plugin.SaveAndReimport();
    }

#if UNITY_IOS
    internal static void Postprocess(UnityEditor.iOS.Xcode.PBXProject project, string output)
    {
        if (!EnabledForExport) return;
        if (PlayerSettings.iOS.sdkVersion != iOSSdkVersion.DeviceSDK)
            throw new InvalidOperationException("Cannot link the EOS iPhoneOS framework into a Simulator export.");
        const string relative = "Frameworks/Idas3EOS/EOSSDK.framework";
        string destination = Path.Combine(output, relative);
        CopyDirectory(Framework, destination);
        string guid = project.FindFileGuidByProjectPath(relative);
        if (string.IsNullOrEmpty(guid))
            guid = project.AddFile(relative, relative, UnityEditor.iOS.Xcode.PBXSourceTree.Source);
        string main = project.GetUnityMainTargetGuid();
        string unity = project.GetUnityFrameworkTargetGuid();
        project.AddFileToBuild(unity, guid);
        UnityEditor.iOS.Xcode.Extensions.PBXProjectExtensions.AddFileToEmbedFrameworks(project, main, guid);
        project.AddBuildProperty(unity, "FRAMEWORK_SEARCH_PATHS", "\"$(PROJECT_DIR)/Frameworks/Idas3EOS\"");
        project.AddBuildProperty(main, "LD_RUNPATH_SEARCH_PATHS", "@executable_path/Frameworks");
        project.SetBuildProperty(main, "ENABLE_BITCODE", "NO");
        project.SetBuildProperty(unity, "ENABLE_BITCODE", "NO");
        project.SetBuildProperty(main, "ALWAYS_EMBED_SWIFT_STANDARD_LIBRARIES", "YES");
        project.AddFrameworkToProject(main, "SafariServices.framework", true);
        project.AddFrameworkToProject(main, "AuthenticationServices.framework", true);
        string raw = Path.Combine(output, "Data/Raw");
        Directory.CreateDirectory(raw);
        File.Copy(Manifest, Path.Combine(raw, "ios-eos-build-id.json"), true);
        string notices = Path.Combine(raw, "EOS-Notices");
        Directory.CreateDirectory(notices);
        foreach (string name in new[] { "license.txt", "ThirdPartySoftwareNotice.txt" })
            File.Copy(Path.Combine("Assets/EOS_SDK", name), Path.Combine(notices, name), true);
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string path in Directory.GetFiles(source))
            if (!path.EndsWith(".meta", StringComparison.Ordinal))
                File.Copy(path, Path.Combine(destination, Path.GetFileName(path)), true);
        foreach (string path in Directory.GetDirectories(source))
            CopyDirectory(path, Path.Combine(destination, Path.GetFileName(path)));
    }
#endif
}
