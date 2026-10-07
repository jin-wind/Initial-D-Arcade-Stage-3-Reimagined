using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Resolve only caller-selected base directories: the running installation and
// the OS temporary directory. Archive entries and paths below those boundaries
// must still pass the staging/helper/cache no-link checks.
public static class Idas3UpdatePaths
{
#if !UNITY_IOS || UNITY_EDITOR
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
    private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint mode,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file,StringBuilder path,uint capacity,uint flags);
#else
    private static SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint mode,uint flags,IntPtr template)=>throw new PlatformNotSupportedException("Desktop updater is unavailable on iOS.");
    private static uint GetFinalPathNameByHandleW(SafeFileHandle file,StringBuilder path,uint capacity,uint flags)=>throw new PlatformNotSupportedException("Desktop updater is unavailable on iOS.");
#endif

    public static string ResolveDirectory(string directory)
    {
        string path=Path.GetFullPath(directory);
        using(var handle=CreateFileW(path,0x80,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero)){
            if(handle.IsInvalid)throw new IOException("Cannot open the update folder.",new Win32Exception(Marshal.GetLastWin32Error()));
            var name=new StringBuilder(32768);
            uint length=GetFinalPathNameByHandleW(handle,name,(uint)name.Capacity,0);
            if(length==0||length>=name.Capacity)throw new IOException("Cannot resolve the update folder.",new Win32Exception(Marshal.GetLastWin32Error()));
            string resolved=name.ToString();
            if(resolved.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))resolved=@"\\"+resolved.Substring(8);
            else if(resolved.StartsWith(@"\\?\",StringComparison.Ordinal))resolved=resolved.Substring(4);
            resolved=Path.GetFullPath(resolved);
            if(!Directory.Exists(resolved))throw new IOException("The update folder is not a directory.");
            return resolved;
        }
    }
}
