using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class ProbeBuild
{
    const string ScenePath = "Assets/ProbeScene.unity";

    [MenuItem("EOS Probe/Build Android ARM64")]
    public static void BuildAndroid()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            throw new InvalidOperationException("Start Unity with -buildTarget Android before invoking this build method.");

        var probeType = Type.GetType("EosMobileProbe, Assembly-CSharp");
        if (probeType == null || !typeof(MonoBehaviour).IsAssignableFrom(probeType))
            throw new InvalidOperationException("EosMobileProbe MonoBehaviour was not found.");

        ConfigurePlayer();
        CreateProbeScene(probeType);

        var output = Environment.GetEnvironmentVariable("IDAS3_EOS_PROBE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
            output = Path.Combine(Application.dataPath, "..", "Builds", "Android", "Idas3EosProbe.apk");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output));

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = output,
            target = BuildTarget.Android,
            options = BuildOptions.None
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException($"Android probe build failed: {report.summary.result}");

        Debug.Log($"EOS Android probe built: {output} ({report.summary.totalSize} bytes)");
    }

    static void ConfigurePlayer()
    {
        var target = UnityEditor.Build.NamedBuildTarget.Android;
        EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;
        EditorUserBuildSettings.buildAppBundle = false;

        PlayerSettings.companyName = "IDAS3 Probe";
        PlayerSettings.productName = "IDAS3 EOS Probe";
        PlayerSettings.SetApplicationIdentifier(target, "com.idas3.eosprobe");
        PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.Low);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64 | AndroidArchitecture.X86_64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel36;
        PlayerSettings.Android.forceInternetPermission = true;
        PlayerSettings.Android.useCustomKeystore = false;
    }

    static void CreateProbeScene(Type probeType)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var cameraObject = new GameObject("Probe Camera");
        var camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        cameraObject.AddComponent<AudioListener>();

        var probeObject = new GameObject("Android EOS Probe");
        probeObject.AddComponent(probeType);

        if (!EditorSceneManager.SaveScene(scene, ScenePath))
            throw new InvalidOperationException("Failed to save the Android probe scene.");
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
    }
}
