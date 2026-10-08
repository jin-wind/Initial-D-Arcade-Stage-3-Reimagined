using System.IO;
using System;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEngine;

public static class Idas3UnityRelayTests
{
    // Compile actual player defines as well as the Editor tests without
    // copying the multi-gigabyte game assets into another installation.
    public static void CheckAndroidPlayer() => CheckPlayer(BuildTarget.Android, BuildTargetGroup.Android);
    public static void CheckIOSPlayer() => CheckPlayer(BuildTarget.iOS, BuildTargetGroup.iOS);

    static void CheckPlayer(BuildTarget target, BuildTargetGroup group)
    {
        Idas3TouchControlsTests.Run();
        Run();
        if (target == BuildTarget.iOS) Idas3IOSBuild.ConfigurePlugins();
        var scripts = PlayerBuildInterface.CompilePlayerScripts(new ScriptCompilationSettings {
            target = target, group = group, options = ScriptCompilationOptions.None
        }, "Temp/RelayPlayerScripts/" + target);
        if (scripts.assemblies == null || scripts.assemblies.Count == 0)
            throw new InvalidOperationException("Player script compilation failed for " + target);
        File.WriteAllText("Logs/unity-relay-player-" + target + ".json", "{\"passed\":true,\"target\":\"" + target +
            "\",\"assemblies\":" + scripts.assemblies.Count + ",\"scope\":\"player C# compilation only; not an IL2CPP package or live Relay test\"}");
        Debug.Log("Unity Relay player scripts compiled for " + target + ": " + scripts.assemblies.Count + " assemblies");
    }

    public static void Run()
    {
        int checks = Idas3.Multiplayer.Idas3UnityRelayChecks.RunSelfTests();
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/unity-relay-tests.json", "{\"passed\":true,\"checks\":" + checks +
            ",\"scope\":\"real UTP UDP loopback with substituted allocation; no live Relay or carrier test\"}");
        Debug.Log("Unity Relay transport checks passed: " + checks);
    }
}
