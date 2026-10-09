using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.Logging;
using Epic.OnlineServices.Platform;
using Epic.OnlineServices.P2P;
using UnityEngine;
using Debug = UnityEngine.Debug;

#if UNITY_ANDROID
[Serializable]
internal sealed class EosProbeConfig
{
    public string productId;
    public string sandboxId;
    public string deploymentId;
    public string clientId;
    public string clientSecret;
    public string role;
    public string runId;
    public int waitSeconds = 240;
}

[Serializable]
internal sealed class EosProbeEvent
{
    public string phase;
    public string result;
    public string role;
    public long elapsedMs;
    public bool hasPuid;
    public bool cleanupOk;
    public bool relayedConnection;
    public bool reliableReceived;
    public bool unreliableReceived;
    public bool reliableAcked;
    public bool unreliableAcked;
    public bool completionHandshake;
    public bool distinctPeer;
    public int members;
    public int payloadBytes;
}

public sealed class EosMobileProbe : MonoBehaviour
{
    const int MaxPacketSize = 1170;
    const string BucketId = "idas3-eos-probe-v1";
    const string AttributeKey = "IDAS3_PROBE";

    static EosMobileProbe instance;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Dictionary<byte, byte[]> outgoing = new Dictionary<byte, byte[]>();
    readonly Dictionary<byte, byte[]> incoming = new Dictionary<byte, byte[]>();
    readonly byte[] receiveBuffer = new byte[MaxPacketSize];

    EosProbeConfig config;
    string reportPath;
    string phase = "startup";
    string display = "Starting EOS probe";
    string role = "unknown";
    string runId;
    string failure;
    string ownedLobby;
    string joinedLobby;
    string peerError;
    PlatformInterface platform;
    LobbyInterface lobbies;
    P2PInterface p2p;
    ProductUserId user;
    ProductUserId peer;
    SocketId socket;
    ulong requestNotification;
    ulong establishedNotification;
    ulong closedNotification;
    bool sdkInitialized;
    bool platformReleased;
    bool lobbyCreateInFlight;
    bool lobbyJoinInFlight;
    bool cleanupStarted;
    bool cleanupOk = true;
    bool relayedConnection;
    bool reliableReceived;
    bool unreliableReceived;
    bool reliableAcked;
    bool unreliableAcked;
    bool remoteDone;
    bool remoteDoneAck;
    bool completionHandshake;
    bool peerClosed;
    bool done;
    float nextLobbyPoll;
    float nextAccept;
    float nextSend;
    float exchangeStartedAt;
    float completedAt;
    int sdkLogCount;

    bool IsHost { get { return role == "host"; } }
    string CurrentLobby { get { return !string.IsNullOrEmpty(ownedLobby) ? ownedLobby : joinedLobby; } }
    bool LocalExchangeComplete { get { return relayedConnection && reliableReceived && unreliableReceived && reliableAcked && unreliableAcked; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (instance != null) return;
        var go = new GameObject("EOS Android Probe");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<EosMobileProbe>();
#endif
    }

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        reportPath = Path.Combine(Application.persistentDataPath, "report.jsonl");
        ReadAndConsumeConfig();
    }

    void Start()
    {
        if (config != null) Run();
        else
        {
            Record("summary", "Failed");
            done = true;
        }
    }

    void Update()
    {
        if (platform != null && !platformReleased)
        {
            try { platform.Tick(); }
            catch { Fail("PlatformTickFailed"); }
        }

        if (lobbies != null && !string.IsNullOrEmpty(CurrentLobby) && user != null && peer == null &&
            !cleanupStarted && failure == null && Time.realtimeSinceStartup >= nextLobbyPoll)
        {
            nextLobbyPoll = Time.realtimeSinceStartup + 0.25f;
            PollLobbyMembers();
        }

        if (p2p != null && peer != null && !done && failure == null)
        {
            try
            {
                ReceivePackets();
                if (peerError != null) Fail(peerError);
                if (failure == null) DriveExchange();
            }
            catch { Fail("P2PExchangeFailed"); }
        }

        if (p2p != null && !done && exchangeStartedAt > 0 && Time.realtimeSinceStartup - exchangeStartedAt > 75f)
            Fail("RelayExchangeTimedOut");
    }

    void ReadAndConsumeConfig()
    {
        string path = Path.Combine(Application.persistentDataPath, "probe-config.json");
        try
        {
            if (!File.Exists(path)) { Fail("MissingProbeConfig"); return; }
            string json = File.ReadAllText(path, Encoding.UTF8);
            EosProbeConfig parsed = JsonUtility.FromJson<EosProbeConfig>(json);
            bool valid = parsed != null && !string.IsNullOrWhiteSpace(parsed.productId) &&
                !string.IsNullOrWhiteSpace(parsed.sandboxId) && !string.IsNullOrWhiteSpace(parsed.deploymentId) &&
                !string.IsNullOrWhiteSpace(parsed.clientId) && !string.IsNullOrWhiteSpace(parsed.clientSecret) &&
                (parsed.role == "host" || parsed.role == "join") && ValidRunId(parsed.runId) &&
                (parsed.waitSeconds == 0 || parsed.waitSeconds >= 30 && parsed.waitSeconds <= 600);
            WipeConfigFile(path);
            if (!valid) { Fail("InvalidProbeConfig"); return; }
            if (parsed.waitSeconds == 0) parsed.waitSeconds = 240;
            config = parsed;
            role = parsed.role;
            runId = parsed.runId;
            display = "Config loaded; credentials removed from disk";
            Record("configuration", "Success");
        }
        catch { Fail("ProbeConfigReadFailed"); }
    }

    static void WipeConfigFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            long length = new FileInfo(path).Length;
            if (length > 0 && length <= 65536)
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    var zeros = new byte[(int)length];
                    stream.Write(zeros, 0, zeros.Length);
                    stream.Flush();
                }
            }
            File.Delete(path);
        }
        catch { }
    }

    static bool ValidRunId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool asciiAlphaNum = c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9';
            if (!asciiAlphaNum && c != '-' && c != '_') return false;
        }
        return true;
    }

    async void Run()
    {
        bool passed = false;
        try
        {
            await InitializeSdk();
            await Authenticate();
            lobbies = platform.GetLobbyInterface();
            p2p = platform.GetP2PInterface();
            if (IsHost) await CreateHostLobby();
            else await FindAndJoinLobby();
            display = "Waiting for both lobby members";
            phase = "waitForLobbyPeer";
            Record(phase, "Waiting");
            nextLobbyPoll = 0;
            await Until(() => peer != null, config.waitSeconds, "PeerRendezvousTimedOut");
            Record(phase, "Success", true, 2, true);
            ConfigureP2P();
            exchangeStartedAt = Time.realtimeSinceStartup;
            display = "Testing forced EOS relay packets";
            phase = "relayExchange";
            await Until(() => done, 75, "RelayExchangeTimedOut");
            Record(phase, "Success", payloadBytes: 100);
            passed = true;
        }
        catch (ProbeFailure exception) { Fail(exception.Code); }
        catch { Fail("UnhandledProbeError"); }
        finally
        {
            await CleanupAsync();
            if (passed && cleanupOk) Record("summary", "Passed", true, 2, true, true);
            else Record("summary", "Failed", user != null, peer != null ? 2 : 0, peer != null, completionHandshake);
            done = true;
            display = passed && cleanupOk ? "EOS relay probe passed" : "EOS relay probe failed: " + (failure ?? "CleanupFailed");
        }
    }

    async Task InitializeSdk()
    {
        phase = "androidJavaInit";
        display = "Initializing Android EOS bridge";
        using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var eos = new AndroidJavaClass("com.epicgames.mobile.eossdk.EOSSDK"))
            eos.CallStatic("init", activity);
        Record(phase, "Success");

        var init = new AndroidInitializeOptions
        {
            ProductName = "IDAS3 EOS Android Probe",
            ProductVersion = "1.0",
            SystemInitializeOptions = new AndroidInitializeOptionsSystemInitializeOptions
            {
                OptionalInternalDirectory = Application.persistentDataPath,
                OptionalExternalDirectory = Application.temporaryCachePath
            }
        };
        phase = "initialize";
        Expect(phase, PlatformInterface.Initialize(ref init));
        sdkInitialized = true;
        LoggingInterface.SetCallback(OnSdkLog);
        LoggingInterface.SetLogLevel(LogCategory.AllCategories, LogLevel.Warning);

        var options = new Epic.OnlineServices.Platform.Options
        {
            ProductId = config.productId,
            SandboxId = config.sandboxId,
            DeploymentId = config.deploymentId,
            ClientCredentials = new ClientCredentials { ClientId = config.clientId, ClientSecret = config.clientSecret },
            IsServer = false,
            Flags = PlatformFlags.DisableOverlay | PlatformFlags.DisableSocialOverlay,
            CacheDirectory = Path.Combine(Application.persistentDataPath, "eos-cache"),
            TickBudgetInMilliseconds = 5,
            TaskNetworkTimeoutSeconds = 20
        };
        phase = "platformCreate";
        platform = PlatformInterface.Create(ref options);
        if (platform == null) throw new ProbeFailure("NullPlatform");
        config.clientSecret = null;
        config.clientId = null;
        config.productId = null;
        config.sandboxId = null;
        config.deploymentId = null;
        Record(phase, "Success");
        await Task.Yield();
    }

    async Task Authenticate()
    {
        var connect = platform.GetConnectInterface();
        var device = new CreateDeviceIdOptions { DeviceModel = "Android EOS mobile probe" };
        phase = "createDeviceId";
        CreateDeviceIdCallbackInfo deviceResult = await Callback<CreateDeviceIdCallbackInfo>(
            callback => connect.CreateDeviceId(ref device, null, (ref CreateDeviceIdCallbackInfo info) => callback(info)), 30);
        Record(phase, deviceResult.ResultCode.ToString());
        if (deviceResult.ResultCode != Result.Success && deviceResult.ResultCode != Result.DuplicateNotAllowed)
            throw new ProbeFailure("DeviceIdFailed");

        var login = new LoginOptions
        {
            Credentials = new Credentials { Type = ExternalCredentialType.DeviceidAccessToken },
            UserLoginInfo = new UserLoginInfo { DisplayName = "IDAS3 Probe" }
        };
        phase = "connectLogin";
        LoginCallbackInfo loginResult = await Callback<LoginCallbackInfo>(
            callback => connect.Login(ref login, null, (ref LoginCallbackInfo info) => callback(info)), 30);
        Record(phase, loginResult.ResultCode.ToString());
        if (loginResult.ResultCode == Result.InvalidUser && loginResult.ContinuanceToken != null)
        {
            var createUser = new CreateUserOptions { ContinuanceToken = loginResult.ContinuanceToken };
            phase = "connectCreateUser";
            CreateUserCallbackInfo created = await Callback<CreateUserCallbackInfo>(
                callback => connect.CreateUser(ref createUser, null, (ref CreateUserCallbackInfo info) => callback(info)), 30);
            Expect(phase, created.ResultCode);
            user = created.LocalUserId;
        }
        else if (loginResult.ResultCode == Result.Success) user = loginResult.LocalUserId;
        else throw new ProbeFailure("LoginFailed");

        if (user == null || !user.IsValid()) throw new ProbeFailure("MissingPuid");
        Record("identity", "Success", true);
        await Task.Yield();
    }

    async Task CreateHostLobby()
    {
        var create = new CreateLobbyOptions
        {
            LocalUserId = user,
            MaxLobbyMembers = 2,
            PermissionLevel = LobbyPermissionLevel.Publicadvertised,
            PresenceEnabled = false,
            AllowInvites = false,
            BucketId = BucketId,
            DisableHostMigration = true,
            EnableRTCRoom = false,
            CrossplayOptOut = false
        };
        phase = "createLobby";
        lobbyCreateInFlight = true;
        CreateLobbyCallbackInfo created = await Callback<CreateLobbyCallbackInfo>(callback =>
            lobbies.CreateLobby(ref create, null, (ref CreateLobbyCallbackInfo info) =>
            {
                if (info.ResultCode == Result.Success) ownedLobby = info.LobbyId;
                lobbyCreateInFlight = false;
                callback(info);
            }), 30);
        Expect(phase, created.ResultCode);
        if (string.IsNullOrEmpty(ownedLobby)) throw new ProbeFailure("MissingLobbyId");

        var updateOptions = new UpdateLobbyModificationOptions { LocalUserId = user, LobbyId = ownedLobby };
        Expect("updateLobbyModification", lobbies.UpdateLobbyModification(ref updateOptions, out var modification));
        try
        {
            var add = new LobbyModificationAddAttributeOptions
            {
                Attribute = new AttributeData { Key = AttributeKey, Value = config.runId },
                Visibility = LobbyAttributeVisibility.Public
            };
            Expect("addAttribute", modification.AddAttribute(ref add));
            var update = new UpdateLobbyOptions { LobbyModificationHandle = modification };
            phase = "updateLobby";
            UpdateLobbyCallbackInfo updated = await Callback<UpdateLobbyCallbackInfo>(callback =>
                lobbies.UpdateLobby(ref update, null, (ref UpdateLobbyCallbackInfo info) => callback(info)), 30);
            Expect(phase, updated.ResultCode);
        }
        finally { modification.Release(); }
        Record("hostReady", "Success");
        display = "Host lobby ready; waiting for join role";
    }

    async Task FindAndJoinLobby()
    {
        var timer = Stopwatch.StartNew();
        while (string.IsNullOrEmpty(joinedLobby) && timer.Elapsed.TotalSeconds < config.waitSeconds)
        {
            var searchOptions = new CreateLobbySearchOptions { MaxResults = 4 };
            Expect("createLobbySearch", lobbies.CreateLobbySearch(ref searchOptions, out var search));
            try
            {
                var parameter = new LobbySearchSetParameterOptions
                {
                    Parameter = new AttributeData { Key = AttributeKey, Value = config.runId },
                    ComparisonOp = ComparisonOp.Equal
                };
                Expect("setSearchParameter", search.SetParameter(ref parameter));
                var find = new LobbySearchFindOptions { LocalUserId = user };
                phase = "findLobby";
                LobbySearchFindCallbackInfo found = await Callback<LobbySearchFindCallbackInfo>(callback =>
                    search.Find(ref find, null, (ref LobbySearchFindCallbackInfo info) => callback(info)), 30);
                Expect(phase, found.ResultCode);
                var countOptions = new LobbySearchGetSearchResultCountOptions();
                uint count = search.GetSearchResultCount(ref countOptions);
                if (count > 1) throw new ProbeFailure("AmbiguousProbeLobbies");
                if (count == 1)
                {
                    var copy = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = 0 };
                    Expect("copySearchResult", search.CopySearchResultByIndex(ref copy, out var details));
                    try
                    {
                        var infoOptions = new LobbyDetailsCopyInfoOptions();
                        Expect("readLobby", details.CopyInfo(ref infoOptions, out var maybeInfo));
                        if (!maybeInfo.HasValue || maybeInfo.Value.MaxMembers != 2 || maybeInfo.Value.LobbyOwnerUserId == user ||
                            (string)maybeInfo.Value.BucketId != BucketId) throw new ProbeFailure("UnexpectedProbeLobby");
                        var attrOptions = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = AttributeKey };
                        Expect("readMarker", details.CopyAttributeByKey(ref attrOptions, out var maybeAttribute));
                        if (!maybeAttribute.HasValue || !maybeAttribute.Value.Data.HasValue ||
                            (string)maybeAttribute.Value.Data.Value.Value.AsUtf8 != config.runId)
                            throw new ProbeFailure("WrongProbeMarker");

                        var join = new JoinLobbyOptions { LocalUserId = user, LobbyDetailsHandle = details, PresenceEnabled = false };
                        phase = "joinLobby";
                        lobbyJoinInFlight = true;
                        JoinLobbyCallbackInfo joined = await Callback<JoinLobbyCallbackInfo>(callback =>
                            lobbies.JoinLobby(ref join, null, (ref JoinLobbyCallbackInfo info) =>
                            {
                                if (info.ResultCode == Result.Success) joinedLobby = info.LobbyId;
                                lobbyJoinInFlight = false;
                                callback(info);
                            }), 30);
                        Expect(phase, joined.ResultCode);
                    }
                    finally { details.Release(); }
                }
            }
            finally { search.Release(); }
            if (string.IsNullOrEmpty(joinedLobby)) await Delay(1000);
        }
        if (string.IsNullOrEmpty(joinedLobby)) throw new ProbeFailure("HostRendezvousTimedOut");
    }

    void PollLobbyMembers()
    {
        try
        {
            var copy = new CopyLobbyDetailsHandleOptions { LocalUserId = user, LobbyId = CurrentLobby };
            if (lobbies.CopyLobbyDetailsHandle(ref copy, out var details) != Result.Success) { Fail("CannotReadCurrentLobby"); return; }
            try
            {
                var countOptions = new LobbyDetailsGetMemberCountOptions();
                uint count = details.GetMemberCount(ref countOptions);
                if (count > 2) { Fail("TooManyLobbyMembers"); return; }
                if (count != 2) return;
                bool foundSelf = false;
                ProductUserId other = null;
                for (uint i = 0; i < count; i++)
                {
                    var member = new LobbyDetailsGetMemberByIndexOptions { MemberIndex = i };
                    ProductUserId id = details.GetMemberByIndex(ref member);
                    if (id == null || !id.IsValid()) { Fail("InvalidMember"); return; }
                    if (id == user) foundSelf = true;
                    else if (other == null) other = id;
                    else { Fail("AmbiguousLobbyPeer"); return; }
                }
                if (!foundSelf || other == null) { Fail("UnexpectedMemberList"); return; }
                peer = other;
                display = "Both authenticated lobby members found";
            }
            finally { details.Release(); }
        }
        catch { Fail("LobbyMemberPollFailed"); }
    }

    void ConfigureP2P()
    {
        var relay = new SetRelayControlOptions { RelayControl = RelayControl.ForceRelays };
        Expect("forceRelays", p2p.SetRelayControl(ref relay));
        socket = new SocketId { SocketName = "Idas3Probe" + Sha256Hex(config.runId).Substring(0, 20) };
        var request = new AddNotifyPeerConnectionRequestOptions { LocalUserId = user, SocketId = socket };
        requestNotification = p2p.AddNotifyPeerConnectionRequest(ref request, null,
            (ref OnIncomingConnectionRequestInfo info) =>
            {
                if (!MatchesPeer(info.LocalUserId, info.RemoteUserId, info.SocketId)) return;
                var accept = new AcceptConnectionOptions { LocalUserId = user, RemoteUserId = peer, SocketId = socket };
                Result result = p2p.AcceptConnection(ref accept);
                if (result != Result.Success) peerError = "AcceptConnection" + result;
            });
        var established = new AddNotifyPeerConnectionEstablishedOptions { LocalUserId = user, SocketId = socket };
        establishedNotification = p2p.AddNotifyPeerConnectionEstablished(ref established, null,
            (ref OnPeerConnectionEstablishedInfo info) =>
            {
                if (!MatchesPeer(info.LocalUserId, info.RemoteUserId, info.SocketId)) return;
                relayedConnection = info.NetworkType == NetworkConnectionType.RelayedConnection;
                Record("peerConnectionEstablished", info.NetworkType.ToString());
                if (!relayedConnection) peerError = "ConnectionWasNotRelayed";
            });
        var closed = new AddNotifyPeerConnectionClosedOptions { LocalUserId = user, SocketId = socket };
        closedNotification = p2p.AddNotifyPeerConnectionClosed(ref closed, null,
            (ref OnRemoteConnectionClosedInfo info) =>
            {
                if (!MatchesPeer(info.LocalUserId, info.RemoteUserId, info.SocketId)) return;
                peerClosed = true;
                Record("peerConnectionClosed", info.Reason.ToString());
                if (!completionHandshake) peerError = "PeerClosedBeforeCompletion";
            });
        if (requestNotification == 0 || establishedNotification == 0 || closedNotification == 0)
            throw new ProbeFailure("P2PNotificationFailed");
        for (byte kind = 1; kind <= 6; kind++)
        {
            outgoing[kind] = Payload(role, kind);
            incoming[kind] = Payload(IsHost ? "join" : "host", kind);
        }
        phase = "relayExchange";
        exchangeStartedAt = Time.realtimeSinceStartup;
        display = "Forcing EOS relay and exchanging packets";
    }

    bool MatchesPeer(ProductUserId local, ProductUserId remote, SocketId? incomingSocket)
    {
        return local == user && remote == peer && incomingSocket.HasValue &&
            incomingSocket.Value.SocketName == socket.SocketName;
    }

    byte[] Payload(string sender, byte kind)
    {
        var bytes = new byte[100];
        bytes[0] = (byte)'I'; bytes[1] = (byte)'D'; bytes[2] = 1; bytes[3] = kind;
        byte[] pattern = Sha256Bytes(runId + ":" + sender + ":" + kind);
        for (int i = 4; i < bytes.Length; i++) bytes[i] = pattern[(i - 4) % pattern.Length];
        return bytes;
    }

    static byte[] Sha256Bytes(string text)
    {
        using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(Encoding.ASCII.GetBytes(text));
    }

    static string Sha256Hex(string text)
    {
        byte[] hash = Sha256Bytes(text);
        var builder = new StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length; i++) builder.Append(hash[i].ToString("x2"));
        return builder.ToString();
    }

    void DriveExchange()
    {
        float now = Time.realtimeSinceStartup;
        if (!relayedConnection && now >= nextAccept)
        {
            var accept = new AcceptConnectionOptions { LocalUserId = user, RemoteUserId = peer, SocketId = socket };
            Result result = p2p.AcceptConnection(ref accept);
            if (result != Result.Success) { Fail("AcceptPeer" + result); return; }
            Send(1, true);
            nextAccept = now + 1f;
        }
        if (relayedConnection && !peerClosed && now >= nextSend)
        {
            if (!reliableAcked) Send(1, false);
            if (!unreliableAcked) Send(2, false);
            if (LocalExchangeComplete)
            {
                Send(5, false);
                if (remoteDone) Send(6, false);
            }
            nextSend = now + 0.25f;
        }
        if (LocalExchangeComplete && remoteDone && remoteDoneAck && !completionHandshake)
        {
            completionHandshake = true;
            completedAt = now;
            Record("completionHandshake", "Success");
        }
        if (completionHandshake && now - completedAt >= (IsHost ? 8f : 5f))
        {
            Record("relayExchange", "Success", payloadBytes: 100);
            done = true;
        }
    }

    void Send(byte kind, bool allowDelayed)
    {
        if (completionHandshake && peerClosed) return;
        var options = new SendPacketOptions
        {
            LocalUserId = user,
            RemoteUserId = peer,
            SocketId = socket,
            Channel = Channel(kind),
            Data = new ArraySegment<byte>(outgoing[kind]),
            Reliability = kind == 2 ? PacketReliability.UnreliableUnordered : PacketReliability.ReliableOrdered,
            AllowDelayedDelivery = allowDelayed || !relayedConnection,
            DisableAutoAcceptConnection = true
        };
        Result result = p2p.SendPacket(ref options);
        if (result != Result.Success) Fail("SendPacket" + result);
    }

    static byte Channel(byte kind) { return kind == 1 ? (byte)0 : kind == 2 ? (byte)1 : (byte)2; }

    void ReceivePackets()
    {
        var options = new ReceivePacketOptions { LocalUserId = user, MaxDataSizeBytes = (uint)receiveBuffer.Length };
        ProductUserId sender = null;
        var sourceSocket = new SocketId();
        for (int i = 0; i < 128; i++)
        {
            byte channel;
            uint bytesWritten;
            Result result = p2p.ReceivePacket(ref options, ref sender, ref sourceSocket, out channel,
                new ArraySegment<byte>(receiveBuffer), out bytesWritten);
            if (result == Result.NotFound) return;
            if (result != Result.Success) { Fail("ReceivePacket" + result); return; }
            if (sender != peer || sourceSocket.SocketName != socket.SocketName) continue;
            byte kind = bytesWritten >= 4 ? receiveBuffer[3] : (byte)0;
            byte[] expected;
            if (bytesWritten != 100 || !incoming.TryGetValue(kind, out expected) || channel != Channel(kind) ||
                !EqualBytes(receiveBuffer, expected)) { Fail("PayloadMismatch"); return; }
            switch (kind)
            {
                case 1: reliableReceived = true; Record("receiveReliable", "Success"); Send(3, true); break;
                case 2: unreliableReceived = true; Record("receiveUnreliable", "Success"); Send(4, true); break;
                case 3: reliableAcked = true; Record("ackReliable", "Success"); break;
                case 4: unreliableAcked = true; Record("ackUnreliable", "Success"); break;
                case 5: remoteDone = true; break;
                case 6: remoteDoneAck = true; break;
            }
            if (failure != null) return;
        }
    }

    static bool EqualBytes(byte[] left, byte[] right)
    {
        if (left.Length < right.Length) return false;
        for (int i = 0; i < right.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    void OnSdkLog(ref LogMessage message)
    {
        if (sdkLogCount++ >= 100) return;
        string category = message.Category == null ? "Unknown" : message.Category.ToString();
        if (category != "P2P" && category != "Lobby" && category != "Connect" && category != "Core" && category != "Http")
            return;
        Record("sdkDiagnostic", message.Level.ToString() + ":" + category);
    }

    async Task CleanupAsync()
    {
        if (cleanupStarted) return;
        cleanupStarted = true;
        if (p2p != null)
        {
            try
            {
                if (requestNotification != 0) p2p.RemoveNotifyPeerConnectionRequest(requestNotification);
                if (establishedNotification != 0) p2p.RemoveNotifyPeerConnectionEstablished(establishedNotification);
                if (closedNotification != 0) p2p.RemoveNotifyPeerConnectionClosed(closedNotification);
                if (peer != null)
                {
                    var close = new CloseConnectionOptions { LocalUserId = user, RemoteUserId = peer, SocketId = socket };
                    Result result = p2p.CloseConnection(ref close);
                    Record("closePeer", result.ToString());
                    cleanupOk &= result == Result.Success || result == Result.NotFound;
                }
            }
            catch { cleanupOk = false; Record("removePeerNotifications", "Failed"); }
        }
        if (platform != null && lobbyCreateInFlight)
        {
            try { await Until(() => !lobbyCreateInFlight, 30, "LobbyCreateCleanupTimedOut", true); }
            catch { cleanupOk = false; Record("settleLobbyCreate", "TimedOut"); }
        }
        if (platform != null && lobbyJoinInFlight)
        {
            try { await Until(() => !lobbyJoinInFlight, 30, "LobbyJoinCleanupTimedOut", true); }
            catch { cleanupOk = false; Record("settleLobbyJoin", "TimedOut"); }
        }
        if (lobbies != null && !string.IsNullOrEmpty(joinedLobby))
        {
            var leave = new LeaveLobbyOptions { LocalUserId = user, LobbyId = joinedLobby };
            try
            {
                LeaveLobbyCallbackInfo left = await Callback<LeaveLobbyCallbackInfo>(callback =>
                    lobbies.LeaveLobby(ref leave, null, (ref LeaveLobbyCallbackInfo info) => callback(info)), 15, true);
                Record("leaveLobby", left.ResultCode.ToString());
                cleanupOk &= left.ResultCode == Result.Success || left.ResultCode == Result.NotFound;
            }
            catch { cleanupOk = false; Record("leaveLobby", "TimedOut"); }
        }
        if (lobbies != null && !string.IsNullOrEmpty(ownedLobby))
        {
            var destroy = new DestroyLobbyOptions { LocalUserId = user, LobbyId = ownedLobby };
            try
            {
                DestroyLobbyCallbackInfo destroyed = await Callback<DestroyLobbyCallbackInfo>(callback =>
                    lobbies.DestroyLobby(ref destroy, null, (ref DestroyLobbyCallbackInfo info) => callback(info)), 15, true);
                Record("destroyLobby", destroyed.ResultCode.ToString());
                cleanupOk &= destroyed.ResultCode == Result.Success || destroyed.ResultCode == Result.NotFound;
            }
            catch { cleanupOk = false; Record("destroyLobby", "TimedOut"); }
        }
        try
        {
            if (platform != null) { platform.Release(); platform = null; }
            if (sdkInitialized) { Result result = PlatformInterface.Shutdown(); Record("shutdown", result.ToString()); cleanupOk &= result == Result.Success; }
        }
        catch { cleanupOk = false; Record("shutdown", "Failed"); }
        if (config != null)
        {
            config.clientSecret = null;
            config.clientId = null;
            config.productId = null;
            config.sandboxId = null;
            config.deploymentId = null;
            config = null;
        }
    }

    async Task<T> Callback<T>(Action<Action<T>> start, int seconds, bool cleanup = false)
    {
        bool complete = false;
        T value = default(T);
        start(result => { value = result; complete = true; });
        await Until(() => complete, seconds, "SdkCallbackTimedOut", cleanup);
        return value;
    }

    async Task Until(Func<bool> condition, int seconds, string timeoutCode, bool cleanup = false)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (!cleanup && failure != null) throw new ProbeFailure(failure);
            if (timer.Elapsed.TotalSeconds > seconds) throw new ProbeFailure(timeoutCode);
            await Task.Yield();
        }
    }

    static async Task Delay(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) await Task.Yield();
    }

    void Fail(string code)
    {
        if (failure != null) return;
        failure = code;
        display = "Failed at " + phase + ": " + code;
        Record(phase, code);
    }

    void Expect(string step, Result result)
    {
        phase = step;
        Record(step, result.ToString());
        if (result != Result.Success) throw new ProbeFailure("OperationFailed");
    }

    void Record(string step, string result, bool hasPuid = false, int members = 0, bool distinctPeer = false,
        bool completion = false, int payloadBytes = 0)
    {
        var evt = new EosProbeEvent
        {
            phase = SafeLabel(step),
            result = SafeLabel(result),
            role = role,
            elapsedMs = clock.ElapsedMilliseconds,
            hasPuid = hasPuid || user != null,
            cleanupOk = cleanupOk,
            relayedConnection = relayedConnection,
            reliableReceived = reliableReceived,
            unreliableReceived = unreliableReceived,
            reliableAcked = reliableAcked,
            unreliableAcked = unreliableAcked,
            completionHandshake = completion || completionHandshake,
            distinctPeer = distinctPeer || peer != null,
            members = members,
            payloadBytes = payloadBytes
        };
        string json = JsonUtility.ToJson(evt);
        Debug.Log("EOS_PROBE " + json);
        try { File.AppendAllText(reportPath, json + "\n", Encoding.UTF8); }
        catch { }
        if (step == "configuration" || step == "platformCreate" || step == "createDeviceId" ||
            step == "connectLogin" || step == "connectCreateUser" || step == "createLobby" ||
            step == "hostReady" || step == "joinLobby" || step == "forceRelays" ||
            step == "peerConnectionEstablished" || step == "relayExchange" || step == "summary")
            ShowStatusToast(SafeLabel(step) + ": " + SafeLabel(result));
    }

    static string SafeLabel(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64) return "Unknown";
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool safe = c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' ||
                c == '_' || c == '-' || c == ':';
            if (!safe) return "Unknown";
        }
        return value;
    }

    void ShowStatusToast(string message)
    {
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var toast = new AndroidJavaClass("android.widget.Toast"))
            {
                AndroidJavaObject instance = toast.CallStatic<AndroidJavaObject>("makeText", activity, message, 0);
                instance.Call("show");
                instance.Dispose();
            }
        }
        catch { }
    }

    void OnApplicationPause(bool paused)
    {
        if (!paused && platform != null && !platformReleased)
        {
            try { platform.SetApplicationStatus(ApplicationStatus.Foreground); }
            catch { }
        }
    }

    void OnApplicationQuit()
    {
        if (!done) Fail("ApplicationQuit");
    }

    void OnDestroy()
    {
        if (!done && !cleanupStarted) Fail("ObjectDestroyed");
    }

    sealed class ProbeFailure : Exception
    {
        public readonly string Code;
        public ProbeFailure(string code) { Code = code; }
    }
}
#endif
