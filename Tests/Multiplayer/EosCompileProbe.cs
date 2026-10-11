// Host-side metadata/configuration probe. Never calls the EOS native SDK.
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class EosCompileProbe
{
    static int checks;
    static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException(description);
        ++checks;
        Console.WriteLine("PASS\t" + description);
    }
    static void Set(Type type, string name, object value) => type.GetField(name).SetValue(null, value);
    public static int Main(string[] args)
    {
        try
        {
            var game = Assembly.LoadFrom(args[0]);
            bool expected = args[1] == "present";
            var runtime = game.GetType("Idas3.Multiplayer.Idas3EosRuntime");
            var transport = game.GetType("Idas3.Multiplayer.Idas3EosTransport");
            Check((runtime != null) == expected, "runtime type presence matches target symbols");
            Check((transport != null) == expected, "transport type presence matches target symbols");
            if (expected)
            {
                var sdk = Assembly.LoadFrom(args[2]);
                string library = (string)sdk.GetType("Epic.OnlineServices.Common").GetField("LIBRARY_NAME").GetRawConstantValue();
                Check(library == args[3], "real SDK library constant matches platform: " + library);
                bool editor = args[4] == "editor";
                if (!editor)
                {
                    var binding = sdk.GetType("Epic.OnlineServices.Bindings").GetMethod("EOS_Initialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    Check(binding.GetCustomAttribute<DllImportAttribute>()?.Value == library,
                        "real SDK EOS_Initialize import uses platform library");
                }
                var application = game.GetType("UnityEngine.Application");
                var platforms = game.GetType("UnityEngine.RuntimePlatform");
                var resources = game.GetType("UnityEngine.Resources");
                var json = game.GetType("UnityEngine.JsonUtility");
                var configured = runtime.GetProperty("Configured", BindingFlags.Static | BindingFlags.NonPublic);
                Func<bool> ready = () => (bool)configured.GetValue(null);
                Set(application, "platform", Enum.Parse(platforms, args[5]));
                Set(application, "isEditor", editor);
                Set(resources, "Asset", null);
                Check(!ready(), "missing configuration fails closed");
                var instance = Activator.CreateInstance(transport);
                Check(!(bool)transport.GetMethod("Initialize").Invoke(instance, null), "transport initialization fails closed without config");
                Check(!(bool)transport.GetProperty("Available").GetValue(instance), "unconfigured transport is unavailable");
                Check(((string)transport.GetProperty("Status").GetValue(instance)).Contains("LAN"), "unconfigured status preserves LAN fallback");
                var configType = runtime.GetNestedType("Config", BindingFlags.NonPublic);
                var config = Activator.CreateInstance(configType, true);
                foreach (var field in configType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    field.SetValue(config, "synthetic-compile-smoke-value");
                Set(json, "Value", config);
                Set(resources, "Asset", Activator.CreateInstance(game.GetType("UnityEngine.TextAsset")));
                Check(ready() == !editor, "synthetic complete config enables mobile player only");
                Set(application, "isEditor", true);
                int loads = (int)resources.GetField("LoadCount").GetValue(null);
                Check(!ready(), "Editor configuration rejected even with EOS symbols and complete config");
                Check((int)resources.GetField("LoadCount").GetValue(null) == loads, "Editor rejection precedes reading a resource");
                Set(application, "isEditor", false);
                Set(application, "platform", Enum.Parse(platforms, "WindowsPlayer"));
                Check(!ready(), "non-mobile runtime platform rejected");
                Set(application, "platform", Enum.Parse(platforms, args[5]));
                Set(json, "Throw", true);
                Check(!ready(), "malformed configuration fails closed");
                Set(json, "Throw", false);
                configType.GetField("clientId").SetValue(config, " ");
                Check(!ready(), "incomplete configuration fails closed");
                ((IDisposable)instance).Dispose();
            }
            Console.WriteLine("TOTAL\t" + checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
