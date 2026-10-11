// Test-only boundaries. Never put this file in Assets. Session, protocol, car
// serialization, course fingerprints, platform paths and EOS are real sources.
using System;
using System.Collections.Generic;
using System.IO;
using Idas3.Multiplayer;

internal static class SessionContractGuard
{
    internal static int UnexpectedCalls;
    internal static Exception Unexpected()
    {
        ++UnexpectedCalls;
        return new InvalidOperationException("Session contract probe crossed an untested boundary.");
    }
}

namespace UnityEngine
{
    public class Object
    {
        protected static void DontDestroyOnLoad(Object value) { throw SessionContractGuard.Unexpected(); }
        protected static void Destroy(Object value) { throw SessionContractGuard.Unexpected(); }
    }
    public class MonoBehaviour : Object { }
    public sealed class GameObject : Object
    {
        public GameObject(string name) { throw SessionContractGuard.Unexpected(); }
        public T AddComponent<T>() { throw SessionContractGuard.Unexpected(); }
    }
    public enum RuntimePlatform { Android, IPhonePlayer, WindowsPlayer, LinuxEditor }
    public static class Application
    {
        public static RuntimePlatform platform;
        public static bool isEditor;
        public static string version, persistentDataPath, streamingAssetsPath, dataPath;
        public static string temporaryCachePath => throw SessionContractGuard.Unexpected();
    }
    public sealed class TextAsset : Object { public string text = "{}"; }
    public static class Resources
    {
        public static T Load<T>(string name) where T : Object
        {
            if (name != "Idas3EOS") throw SessionContractGuard.Unexpected();
            return null; // No credentials or configured EOS resources exist.
        }
    }
    public static class JsonUtility
    {
        public static T FromJson<T>(string text) { throw SessionContractGuard.Unexpected(); }
        public static string ToJson(object value, bool prettyPrint = false) { throw SessionContractGuard.Unexpected(); }
    }
    public static class Time { public static double realtimeSinceStartupAsDouble => 0; }
    public static class Random { public static float value => throw SessionContractGuard.Unexpected(); }
    public static class Debug
    {
        public static void Log(object value) { }
        public static void LogWarning(object value) { }
        public static void LogError(object value) { }
    }
    public static class Mathf
    {
        public const float Rad2Deg = 57.29578f, Deg2Rad = .0174532924f;
        public static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));
        public static float Abs(float value) => Math.Abs(value);
        public static float Clamp01(float value) { throw SessionContractGuard.Unexpected(); }
        public static float Lerp(float a, float b, float t) { throw SessionContractGuard.Unexpected(); }
        public static float LerpAngle(float a, float b, float t) { throw SessionContractGuard.Unexpected(); }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { throw SessionContractGuard.Unexpected(); }
    }
    public struct Vector4
    {
        public float this[int index] { get => throw SessionContractGuard.Unexpected(); set => throw SessionContractGuard.Unexpected(); }
        public static Vector4 Lerp(Vector4 a, Vector4 b, float t) { throw SessionContractGuard.Unexpected(); }
    }
#if UNITY_ANDROID
    // Deliberately unavailable to the iOS compilation.
    public class AndroidJavaObject : IDisposable { public void Dispose() { } }
    public sealed class AndroidJavaClass : AndroidJavaObject
    {
        public AndroidJavaClass(string name) { throw SessionContractGuard.Unexpected(); }
        public T CallStatic<T>(string method, params object[] args) { throw SessionContractGuard.Unexpected(); }
        public void CallStatic(string method, params object[] args) { throw SessionContractGuard.Unexpected(); }
        public T GetStatic<T>(string name) { throw SessionContractGuard.Unexpected(); }
    }
#endif
}

// These scene/music boundaries are compiled but must not be exercised.
internal static class Idas3Native
{
    internal const string Library = "IDAS3_CONTRACT_TEST_NATIVE_CALLS_FORBIDDEN";
    internal struct Status { internal uint flags; internal int course; }
    internal static string Error() { throw SessionContractGuard.Unexpected(); }
    internal static int Idas3SceneSetPreRaceNames(string local, string remote) { throw SessionContractGuard.Unexpected(); }
}
internal static class Idas3CustomRaceMusic { internal const string FolderName = "Custom Music"; }
internal static class IdasSpecialStageEnnaCourse
{
    internal const uint SceneFlag = 32768;
    internal static int CourseId(uint flags) { throw SessionContractGuard.Unexpected(); }
}
internal static class Idas8HakoneCourse
{
    internal static int CourseId(uint flags) { throw SessionContractGuard.Unexpected(); }
}

namespace Idas3.Multiplayer
{
    // Only used to compile unrelated UI/presence calls, not asserted by this test.
    public struct Idas3OnlineActivity
    {
        internal static string StateFor(bool racing, string state, bool quick, bool lobby, bool connected, bool disconnected)
        { throw SessionContractGuard.Unexpected(); }
    }
    public static class Idas3LobbyNames
    {
        public static string ForHost(string name) { throw SessionContractGuard.Unexpected(); }
    }

    // In-memory peer wire. No socket, service, account, discovery or native calls.
    public class SessionContractTransport : IIdas3Transport
    {
        public string Kind => "Contract memory wire";
        public bool Available => true;
        public bool Connected { get; set; } = true;
        public bool IsHost { get; set; }
        public string LocalId => "synthetic-local";
        public string LocalName => IsHost ? "Android host" : "iOS guest";
        public string RemoteId => "synthetic-remote";
        public string RemoteName => "Other driver";
        public string RoomCode => Connected ? "SYNTHETIC" : "";
        public string Status => "Synthetic peer boundary";
        public IReadOnlyList<Idas3Room> Rooms => Array.Empty<Idas3Room>();
        public event Action<byte[]> Message;
        public event Action PeerChanged;
        public event Action<string> Error { add { } remove { } }
        public readonly List<byte[]> Sent = new List<byte[]>();
        public bool Initialize() { throw SessionContractGuard.Unexpected(); }
        public void Host(string name) { throw SessionContractGuard.Unexpected(); }
        public void Join(string code) { throw SessionContractGuard.Unexpected(); }
        public void Browse() { throw SessionContractGuard.Unexpected(); }
        public void Send(byte[] data, bool reliable) { Sent.Add((byte[])data.Clone()); }
        public void Poll() { }
        public void Leave() { Connected = false; }
        public void Dispose() { }
        public void Connect() { PeerChanged?.Invoke(); }
        public void Deliver(byte[] data) { Message?.Invoke(data); }
    }
    // Provider placeholders allow only construction/type inspection. Tests use
    // SessionContractTransport explicitly, never these providers for peer I/O.
    public sealed class Idas3SteamTransport : SessionContractTransport
    {
        internal bool PublishActivity, ActivityRequested;
        internal string ActivityState;
        internal Idas3OnlineActivity Activity => throw SessionContractGuard.Unexpected();
    }
    public sealed class Idas3TcpTransport : SessionContractTransport
    {
        internal string BuildCompatibility;
        public Idas3TcpTransport() { }
        public Idas3TcpTransport(int port, bool local, string name) { }
    }
    public sealed class Idas3ChallengerTestTransport : SessionContractTransport
    { public Idas3ChallengerTestTransport(int port, string name) { } }
    public sealed class Idas3UnityRelayTransport : SessionContractTransport { }
}
