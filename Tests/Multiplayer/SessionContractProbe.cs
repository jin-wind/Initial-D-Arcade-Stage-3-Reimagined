using System;
using System.IO;
using System.Reflection;
using System.Text;
using Idas3.Multiplayer;
using UnityEngine;

// Compiled alongside unmodified production sources, never part of the player.
public static class SessionContractProbe
{
    const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    static string root;
    static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException(description);
        Console.WriteLine("PASS\t" + description);
    }
    static object Invoke(Idas3MultiplayerSession session, string name, params object[] args) =>
        typeof(Idas3MultiplayerSession).GetMethod(name, PrivateInstance).Invoke(session, args);
    static string Identity(Idas3MultiplayerSession session) => (string)Invoke(session, "Compatibility");
    static Idas3MultiplayerSession Session() => new Idas3MultiplayerSession(0, Path.Combine(root, "absent-saves"));
    static Idas3MultiplayerSession Peer(bool host, out SessionContractTransport wire)
    {
        var session = Session();
        var car = new Idas3OnlineCar { Selection = 5, Automatic = true };
        typeof(Idas3MultiplayerSession).GetProperty("LocalSavedCar").SetValue(session, car);
        wire = new SessionContractTransport { IsHost = host };
        Invoke(session, "SetTransport", wire);
        wire.Connect(); // Production event handler constructs/serializes Hello.
        Check(wire.Sent.Count == 1 && wire.Sent[0][6] == 1, "Production peer callback emits exactly one Hello");
        using (var reader = new BinaryReader(new MemoryStream(wire.Sent[0])))
            Check(reader.ReadUInt32() == 0x504D3349 && reader.ReadUInt16() == 11 && reader.ReadByte() == 1 &&
                reader.ReadUInt64() != 0 && reader.ReadUInt64() == 0, "Hello uses pinned protocol 11, magic and nonce envelope");
        return session;
    }
    static void Reject(byte[] packet, bool host, string expectedError, string description)
    {
        using (var session = Peer(host, out var wire))
        {
            wire.Deliver(packet); // Production event handler parses the packet.
            Check(!session.HandshakeComplete && !wire.Connected && session.StateName == "Offline" &&
                session.ErrorText.Contains(expectedError), description);
        }
    }
    static byte[] ChangeBuild(byte[] packet, string suffix)
    {
        using (var input = new MemoryStream(packet))
        using (var reader = new BinaryReader(input, Encoding.UTF8))
        using (var output = new MemoryStream())
        using (var writer = new BinaryWriter(output, Encoding.UTF8))
        {
            writer.Write(reader.ReadBytes(23));
            writer.Write(reader.ReadString() + suffix);
            writer.Write(reader.ReadBytes((int)(input.Length - input.Position)));
            return output.ToArray();
        }
    }
    static void Accept(byte[] packet, bool host, string description)
    {
        using (var session = Peer(host, out var wire))
        {
            wire.Deliver(packet);
            Check(session.HandshakeComplete && session.StateName == "Lobby" && session.ErrorText == "" &&
                wire.Connected && session.RemoteSavedCar.Car == 0 && session.RemoteSavedCar.Automatic, description);
            Check(!session.IsRacing && !session.CanStart && !session.RaceReleased,
                "Hello admission alone cannot start or release a race");
            if (host) Check(wire.Sent.Count == 2 && wire.Sent[1][6] == 2, "Admitted guest receives production Lobby reply");
        }
    }
    static void ResetVersionCache() => typeof(Idas3PlatformPaths).GetField("applicationVersion", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
    static void CheckVersionFallback(string expected)
    {
        string label = Path.Combine(Application.streamingAssetsPath, "idas3-app-version.txt");
        byte[] original = File.ReadAllBytes(label);
        string oldVersion = Application.version;
        try
        {
            if (Application.platform == RuntimePlatform.IPhonePlayer)
            {
                foreach (bool absent in new[] { true, false })
                {
                    if (absent) File.Delete(label); else File.WriteAllText(label, " \n\t");
                    ResetVersionCache();
                    using (var fallback = Session()) Check(Idas3PlatformPaths.ApplicationVersion == Application.version && Identity(fallback) != expected,
                        "iOS " + (absent ? "missing" : "blank") + " release label falls back to numeric version and separates builds");
                }
                File.WriteAllBytes(label, original);
                Application.version = "9.9.9";
                ResetVersionCache();
                using (var renamed = Session()) Check(Identity(renamed) == expected, "Packaged iOS release label takes precedence over numeric app version");
                File.WriteAllText(label, "0.3.96-contract\n");
            }
            else Application.version = "0.3.96-contract";
            ResetVersionCache();
            using (var changed = Session()) Check(Identity(changed) != expected &&
                Idas3BuildCompatibility.ForMatchmaking(Identity(changed)) != File.ReadAllText(Path.Combine(root, "expected-key.txt")),
                "A different canonical release label changes the identity and matchmaking key");
        }
        finally { File.WriteAllBytes(label, original); Application.version = oldVersion; ResetVersionCache(); }
    }
    public static void Run(string[] args)
    {
        root = args[0];
        Application.platform = (RuntimePlatform)Enum.Parse(typeof(RuntimePlatform), args[1]);
        Application.isEditor = args[2] == "editor";
        Application.version = Application.platform == RuntimePlatform.IPhonePlayer ? "0.3.95" : "0.3.95-contract";
        Application.persistentDataPath = Path.Combine(root, "persistent");
        Application.streamingAssetsPath = Path.Combine(root, "streaming");
        Application.dataPath = Path.Combine(root, "player", "InitialDUnity_Data");
        string expected = File.ReadAllText(Path.Combine(root, "expected-identity.txt"));
        bool mobile = args[1] == "Android" || args[1] == "IPhonePlayer";

        using (var session = Session())
        {
            Check(session.TransportIndex == (mobile ? 2 : 0), "Compiled session chooses expected default transport index");
            Check(session.ExperimentalAuthority, "Shared authority simulation remains the default");
            string identity = Identity(session);
            Check(identity == expected, "Production compatibility identity equals independent Python fixture");
            string key = Idas3BuildCompatibility.ForMatchmaking(identity);
            Check(key == File.ReadAllText(Path.Combine(root, "expected-key.txt")) && key.Length == 57 &&
                Idas3BuildCompatibility.IsValidMatchmakingKey(key), "Production matchmaking key equals independent fixture");
            Check(Idas3PlatformPaths.ApplicationVersion == "0.3.95-contract", "Canonical release label is identical across platforms");
            if (Application.platform == RuntimePlatform.IPhonePlayer)
                Check(Application.version != Idas3PlatformPaths.ApplicationVersion, "iOS numeric app version is restored from packaged release label");
        }
        var factory = typeof(Idas3MultiplayerSession).GetMethod("CreateTransport", BindingFlags.Static | BindingFlags.NonPublic);
        using (var selected = (IIdas3Transport)factory.Invoke(null, new object[] { 2 }))
        {
            Check(selected.GetType().Name == args[3], "Compiled Internet factory selects " + args[3]);
            if (args[3] == "Idas3EosTransport")
                Check(!selected.Initialize() && !selected.Available && selected.Status.Contains("not configured"),
                    "Real EOS transport safely rejects absent configuration without native initialization");
        }
        foreach (int invalid in new[] { -1, 3 })
        {
            bool rejected = false;
            try { factory.Invoke(null, new object[] { invalid }); }
            catch (TargetInvocationException error) { rejected = error.InnerException is ArgumentOutOfRangeException; }
            Check(rejected, "Compiled factory rejects invalid index " + invalid);
        }

        if (args.Length > 4)
        {
            byte[] remoteHost = File.ReadAllBytes(args[4]), remoteGuest = File.ReadAllBytes(args[5]);
            Accept(remoteHost, false, "Guest accepts Hello generated by the other compiled mobile platform");
            Accept(remoteGuest, true, "Host accepts Hello generated by the other compiled mobile platform");
            foreach (bool host in new[] { false, true })
            {
                byte[] source = host ? remoteGuest : remoteHost;
                Reject(ChangeBuild(source, "-different-game"), host, "same game version", "Different full build identity closes the peer");
                byte[] protocol = (byte[])source.Clone(); protocol[4] ^= 1;
                Reject(protocol, host, "different multiplayer protocol", "Different wire protocol closes the peer");
                byte[] nonce = (byte[])source.Clone(); Array.Clear(nonce, 7, 8);
                Reject(nonce, host, "Invalid handshake", "Zero sender nonce closes the peer");
                byte[] recipient = (byte[])source.Clone(); recipient[15] = 1;
                Reject(recipient, host, "Invalid handshake", "Addressed Hello closes the peer");
                byte[] extra = new byte[source.Length + 1]; Array.Copy(source, extra, source.Length);
                Reject(extra, host, "Unexpected extra packet data", "Trailing Hello bytes cannot leave an admitted peer");
                byte[] shortPacket = new byte[22];
                Reject(shortPacket, host, "Invalid packet size", "Truncated header closes the peer");
            }
        }
        else
        {
            CheckVersionFallback(expected);
            string fingerprintPath = Idas3PlatformPaths.NativePluginFingerprintPath;
            Directory.CreateDirectory(Path.GetDirectoryName(fingerprintPath));
            File.WriteAllText(fingerprintPath, "synthetic native binary/build-id unique to " + args[1]);
            using (var packaged = Session()) Check(Identity(packaged) == expected,
                "Platform-specific native binary/build-id bytes do not split compatible sessions");
            for (int course = 11; course < Idas3CourseCatalog.Count; ++course)
            {
                string folder = Idas3PlatformPaths.RuntimePackPath(Idas3CourseCatalog.Packs[course - 9]);
                foreach (string file in Directory.GetFiles(folder))
                {
                    if (Path.GetFileName(file) == "menu.idastex") continue;
                    byte[] original = File.ReadAllBytes(file);
                    try
                    {
                        File.WriteAllBytes(file, new byte[] { 99, 1, 2, 3 });
                        using (var changed = Session()) Check(Identity(changed) != expected &&
                            Idas3BuildCompatibility.ForMatchmaking(Identity(changed)) != File.ReadAllText(Path.Combine(root, "expected-key.txt")),
                            "Simulation input changes identity and key: " + course + "/" + Path.GetFileName(file));
                    }
                    finally { File.WriteAllBytes(file, original); }
                }
                string marker = Path.Combine(folder, "menu.idastex");
                byte[] menu = File.ReadAllBytes(marker);
                try
                {
                    File.Delete(marker);
                    using (var missing = Session()) Check(Identity(missing) != expected, "Absent course changes identity: " + course);
                }
                finally { File.WriteAllBytes(marker, menu); }
            }
            string failedFile = Path.Combine(Idas3PlatformPaths.RuntimePackPath("ENNA"), "course.id");
            byte[] restore = File.ReadAllBytes(failedFile);
            using (var retry = Session())
            {
                try
                {
                    File.Delete(failedFile);
                    bool failed = false;
                    try { Identity(retry); } catch (TargetInvocationException error) { failed = error.InnerException is IOException; }
                    Check(failed, "Incomplete installed course cannot silently create a compatibility identity");
                }
                finally { File.WriteAllBytes(failedFile, restore); }
                Check(Identity(retry) == expected, "Failed course read does not cache a partial identity");
            }
            foreach (bool host in new[] { false, true })
            {
                using (var sender = Peer(host, out var wire))
                {
                    File.WriteAllBytes(Path.Combine(root, host ? "host-hello.bin" : "guest-hello.bin"), wire.Sent[0]);
                    Accept(wire.Sent[0], !host, "Same-platform compiled Hello is accepted");
                }
            }
        }
        Check(!Directory.Exists(Path.Combine(root, "absent-saves")), "Handshake and rejection probes never create or modify save records");
        Check(SessionContractGuard.UnexpectedCalls == 0, "No untested Unity, scene, JSON, JNI or service boundary was attempted");
    }
}
