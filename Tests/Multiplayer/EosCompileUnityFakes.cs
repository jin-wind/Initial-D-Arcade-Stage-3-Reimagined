// Compile-only Unity boundary. There are deliberately no EOS API substitutes.
// This is not an implementation of Unity and must never enter the Assets tree.
using System;

namespace UnityEngine
{
    public class Object
    {
        protected static void DontDestroyOnLoad(Object value) { throw Unexpected(); }
        protected static void Destroy(Object value) { throw Unexpected(); }
        internal static Exception Unexpected() => new InvalidOperationException("Smoke probe attempted runtime initialization.");
    }
    public class MonoBehaviour : Object { }
    public sealed class GameObject : Object
    {
        public GameObject(string name) { throw Unexpected(); }
        public T AddComponent<T>() { throw Unexpected(); }
    }
    public enum RuntimePlatform { Android, IPhonePlayer, WindowsPlayer, LinuxEditor }
    public static class Application
    {
        public static RuntimePlatform platform;
        public static bool isEditor;
        public static string persistentDataPath => throw Object.Unexpected();
        public static string temporaryCachePath => throw Object.Unexpected();
    }
    public sealed class TextAsset : Object { public string text = "{}"; }
    public static class Resources
    {
        public static Object Asset;
        public static int LoadCount;
        public static T Load<T>(string name) where T : Object
        {
            if (name != "Idas3EOS") throw new InvalidOperationException("Unexpected resource.");
            ++LoadCount;
            return (T)Asset;
        }
    }
    public static class JsonUtility
    {
        public static object Value;
        public static bool Throw;
        public static T FromJson<T>(string text)
        {
            if (Throw) throw new ArgumentException("Synthetic malformed JSON.");
            return (T)Value;
        }
    }
    public static class Time { public static double realtimeSinceStartupAsDouble => 0; }
    public static class Debug
    {
        public static void Log(object value) { }
        public static void LogWarning(object value) { }
    }
#if UNITY_ANDROID
    // Deliberately absent in the iOS compile, catching accidental JNI leakage.
    public class AndroidJavaObject : IDisposable
    {
        public void Dispose() { }
    }
    public sealed class AndroidJavaClass : AndroidJavaObject
    {
        public AndroidJavaClass(string name) { throw Object.Unexpected(); }
        public T CallStatic<T>(string method, params object[] args) { throw Object.Unexpected(); }
        public void CallStatic(string method, params object[] args) { throw Object.Unexpected(); }
        public T GetStatic<T>(string name) { throw Object.Unexpected(); }
    }
#endif
}
