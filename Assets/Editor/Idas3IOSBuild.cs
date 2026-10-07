using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

// Export only. Run Xcode/simctl separately on the Mac after inspecting logs.
public static class Idas3IOSBuild
{
    const string Scene = "Assets/Scenes/InitialDUnityScene.unity";
    const string Native = "Assets/Plugins/iOS/libIdas3Unity.a";
    const string Manifest = "Assets/Plugins/iOS/Idas3NativeBuild.json";
    [Serializable] sealed class NativeManifest
    {
        public string sdk;
        public string[] architectures;
        public string minimumOS;
    }

    [MenuItem("Initial D/iOS/Export ARM64 Simulator Xcode Project")]
    public static void BuildSimulator() => Build(true);

    [MenuItem("Initial D/iOS/Export ARM64 Device Xcode Project")]
    public static void BuildDevice() => Build(false);

    public static void RunChecks()
    {
        Idas3TouchControlsTests.Run();
        Idas3MetalGeometryChecks.Run();
    }

    public static void BuildSimulatorChecked()
    {
        RunChecks();
        BuildSimulator();
    }

    public static void BuildDeviceChecked()
    {
        RunChecks();
        BuildDevice();
    }

    static void Build(bool simulator)
    {
        if (Application.platform != RuntimePlatform.OSXEditor)
            throw new InvalidOperationException("Export the iOS project on the remote Mac.");
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.iOS, BuildTarget.iOS))
            throw new InvalidOperationException("Install iOS Build Support for this exact Unity Editor version.");
        string sdk = simulator ? "iphonesimulator" : "iphoneos";
        foreach (string path in new[] { Scene, Native, Manifest,
            "Assets/StreamingAssets/IDAS3/data.manifest.json",
            "Assets/StreamingAssets/IDAS3/data/original_physics/fsca_table.bin" })
            if (!File.Exists(path)) throw new FileNotFoundException("Missing staged iOS input.", path);
        var native = JsonUtility.FromJson<NativeManifest>(File.ReadAllText(Manifest));
        if (native == null || native.sdk != sdk || native.architectures == null ||
            Array.IndexOf(native.architectures, "arm64") < 0)
            throw new InvalidOperationException("Native archive SDK/architecture mismatch. Run Tools/build-native-ios.sh " + sdk);
        if (!Version.TryParse(native.minimumOS, out var minOS) || minOS > new Version(16, 3))
            throw new InvalidOperationException("Native deployment target must be 16.3 or older for this build profile.");
        if (!File.Exists("Assets/StreamingAssets/rom/gds-0033.chd"))
            Debug.LogWarning("No bundled ROM. Startup remains blocked until a valid ROM is seeded in the app Documents/game/rom container.");
        if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS))
            throw new InvalidOperationException("Could not activate the iOS build target.");

        PlayerSettings.SetScriptingBackend(NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, "com.idas3.unity.ios");
        // Unity rejects non-numeric iOS versions, but online peers, replays and
        // community times compare the full release label. Export with its
        // numeric prefix and trailing build number, and bake the label for
        // Idas3PlatformPaths.ApplicationVersion; the label is restored afterwards.
        string label = PlayerSettings.bundleVersion;
        var numeric = System.Text.RegularExpressions.Regex.Match(label, @"^\d+(\.\d+){0,2}");
        if (!numeric.Success)
            throw new InvalidOperationException("Project version must start with a numeric X.Y.Z: " + label);
        var buildNumber = System.Text.RegularExpressions.Regex.Match(label.Substring(numeric.Length), @"(\d+)$");
        PlayerSettings.iOS.buildNumber = buildNumber.Success ? buildNumber.Groups[1].Value : "1";
        PlayerSettings.SetArchitecture(NamedBuildTarget.iOS, 1); // ARM64 device.
        PlayerSettings.iOS.sdkVersion = simulator ? iOSSdkVersion.SimulatorSDK : iOSSdkVersion.DeviceSDK;
        PlayerSettings.iOS.simulatorSdkArchitecture = AppleMobileArchitectureSimulator.ARM64;
        PlayerSettings.iOS.targetOSVersionString = "16.3";
        PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPhoneAndiPad;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.iOS, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS, new[] { GraphicsDeviceType.Metal });
        PlayerSettings.colorSpace = ColorSpace.Gamma;
        PlayerSettings.allowUnsafeCode = true;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.runInBackground = false;
        QualitySettings.vSyncCount = 1;
        QualitySettings.antiAliasing = 2;
        GraphicsSettings.defaultRenderPipeline = null;
        int quality = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; ++i)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = null;
        }
        QualitySettings.SetQualityLevel(quality, false);
        var graphics = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
        var stripping = graphics.FindProperty("m_InstancingStripping");
        if (stripping != null) { stripping.intValue = 2; graphics.ApplyModifiedPropertiesWithoutUndo(); }
        // Preserve both input paths used by the existing project.
        var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
        var input = settings.FindProperty("activeInputHandler");
        if (input != null) { input.intValue = 2; settings.ApplyModifiedPropertiesWithoutUndo(); }

        ConfigurePlugins();
        File.Copy(Manifest, "Assets/StreamingAssets/ios-native-build-id.json", true);
        File.WriteAllText("Assets/StreamingAssets/idas3-app-version.txt", label);
        AssetDatabase.Refresh();
        AssetDatabase.SaveAssets();
        string output = Environment.GetEnvironmentVariable("IDAS3_IOS_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) output = simulator ? "Builds/iOS-Simulator" : "Builds/iOS-Device";
        Directory.CreateDirectory(output);
        BuildReport report;
        PlayerSettings.bundleVersion = numeric.Value;
        try
        {
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { Scene }, locationPathName = output, target = BuildTarget.iOS,
                options = simulator ? BuildOptions.Development : BuildOptions.None
            });
        }
        finally
        {
            PlayerSettings.bundleVersion = label;
            AssetDatabase.SaveAssets();
        }
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("iOS Xcode export failed: " + report.summary.result);
        Debug.Log("Exported " + sdk + " Xcode project: " + Path.GetFullPath(output) + ". Not yet compiled or run.");
    }

    [MenuItem("Initial D/iOS/Configure Native Plugin Platforms")]
    public static void ConfigurePlugins()
    {
        AssetDatabase.Refresh();
        foreach (var importer in PluginImporter.GetAllImporters())
        {
            if (!importer.isNativePlugin) continue;
            string path = importer.assetPath.Replace('\\', '/');
            if (path.StartsWith("Assets/Plugins/iOS/", StringComparison.Ordinal))
                Configure(importer, BuildTarget.iOS, "ARM64", false, "", "");
            else if (path.StartsWith("Assets/Plugins/macOS/", StringComparison.Ordinal))
                Configure(importer, BuildTarget.StandaloneOSX, "ARM64", true, "OSX", "ARM64");
            else if (path.StartsWith("Assets/Plugins/x86_64/", StringComparison.Ordinal))
                Configure(importer, BuildTarget.StandaloneWindows64, "x86_64", true, "Windows", "x86_64");
            else
            {
                // Android .so/AAR and desktop Steam runtimes must not enter
                // the iOS Xcode project; preserve their existing other targets.
                importer.SetExcludeFromAnyPlatform(BuildTarget.iOS, true);
                importer.SetCompatibleWithPlatform(BuildTarget.iOS, false);
                importer.SaveAndReimport();
            }
        }
    }

    static void Configure(PluginImporter plugin, BuildTarget target, string cpu, bool editor, string editorOS, string editorCPU)
    {
        plugin.SetCompatibleWithAnyPlatform(false);
        plugin.SetCompatibleWithEditor(editor);
        foreach (BuildTarget other in new[] { BuildTarget.iOS, BuildTarget.Android, BuildTarget.StandaloneOSX,
            BuildTarget.StandaloneWindows64, BuildTarget.StandaloneLinux64 })
            plugin.SetCompatibleWithPlatform(other, other == target);
        plugin.SetPlatformData(target, "CPU", cpu);
        if (editor) { plugin.SetEditorData("OS", editorOS); plugin.SetEditorData("CPU", editorCPU); }
        plugin.SaveAndReimport();
    }
}

#if UNITY_IOS
// Kept under UNITY_IOS so editors without the iOS extension can still compile
// the project. No Android tooling is invoked or copied into the Xcode output.
public sealed class Idas3IOSPostprocess : IPostprocessBuildWithReport
{
    public int callbackOrder => 200;
    public void OnPostprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.iOS) return;
        string output = report.summary.outputPath;
        var plist = new UnityEditor.iOS.Xcode.PlistDocument();
        string plistPath = Path.Combine(output, "Info.plist");
        plist.ReadFromFile(plistPath);
        plist.root.SetString("NSLocalNetworkUsageDescription", "Connect directly to another Initial D player on your local network.");
        plist.root.SetBoolean("UIFileSharingEnabled", true);
        plist.root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
        plist.WriteToFile(plistPath);
        var project = new UnityEditor.iOS.Xcode.PBXProject();
        string projectPath = UnityEditor.iOS.Xcode.PBXProject.GetPBXProjectPath(output);
        project.ReadFromFile(projectPath);
        string framework = project.GetUnityFrameworkTargetGuid();
        string nativeRelative = "Libraries/Plugins/iOS/libIdas3Unity.a";
        string nativeOutput = Path.Combine(output, nativeRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(nativeOutput));
        File.Copy(Path.GetFullPath("Assets/Plugins/iOS/libIdas3Unity.a"), nativeOutput, true);
        string nativeGuid = project.AddFile(nativeRelative, "libIdas3Unity.a", UnityEditor.iOS.Xcode.PBXSourceTree.Source);
        // Keep the archive out of the default Frameworks phase. Unity turns
        // that phase into `-lIdas3Unity`, which requires a search path; the
        // explicit force-load flag below uses the copied archive path instead.
        project.AddBuildProperty(framework, "OTHER_LDFLAGS", "-force_load $(SRCROOT)/" + nativeRelative);
        project.AddFrameworkToProject(framework, "libc++.tbd", false);
        project.AddFrameworkToProject(framework, "libz.tbd", false);
        // Copy Bundle Resources makes a full copy of the ~5 GiB Data folder on
        // every build, which this Mac's disk cannot spare. Clone it on APFS
        // instead; script sandboxing would otherwise hide Data from the phase.
        string main = project.GetUnityMainTargetGuid();
        string data = project.FindFileGuidByProjectPath("Data");
        if (!string.IsNullOrEmpty(data))
        {
            project.RemoveFileFromBuild(main, data);
            project.AddShellScriptBuildPhase(main, "Clone Unity Data", "/bin/sh",
                "dst=\"$TARGET_BUILD_DIR/$UNLOCALIZED_RESOURCES_FOLDER_PATH/Data\"\n" +
                "rm -rf \"$dst\" && cp -cR \"$PROJECT_DIR/Data\" \"$dst\"\n");
            project.SetBuildProperty(main, "ENABLE_USER_SCRIPT_SANDBOXING", "NO");
        }
        project.WriteToFile(projectPath);
    }
}
#endif
