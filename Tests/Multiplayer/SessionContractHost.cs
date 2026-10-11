// .NET 8 host only. Production/test sources separately compile as netstandard2.1.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

internal static class SessionContractHost
{
    public static int Main(string[] args)
    {
        int nativeAttempts = 0;
        void DenyNative(Assembly assembly)
        {
            NativeLibrary.SetDllImportResolver(assembly, (name, origin, paths) =>
            {
                ++nativeAttempts;
                throw new InvalidOperationException("Native calls forbidden in contract probe: " + name);
            });
        }
        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(args[0]));
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                string path = Path.Combine(folder, name.Name + ".dll");
                if (!File.Exists(path)) return null;
                Assembly dependency = context.LoadFromAssemblyPath(path);
                DenyNative(dependency);
                return dependency;
            };
            Assembly game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
            DenyNative(game);
            string[] probeArgs = new string[args.Length - 1];
            Array.Copy(args, 1, probeArgs, 0, probeArgs.Length);
            game.GetType("SessionContractProbe", true).GetMethod("Run").Invoke(null, new object[] { probeArgs });
            if (nativeAttempts != 0) throw new InvalidOperationException("A native call was attempted and caught by production code.");
            Console.WriteLine("PASS\tNo native calls were attempted");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
