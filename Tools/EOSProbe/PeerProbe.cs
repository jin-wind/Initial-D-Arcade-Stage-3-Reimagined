using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.P2P;

internal static partial class Program
{
    private static string role = "single", runId, joinedLobby, peerError;
    private static bool joinInFlight, relayedConnection, reliableReceived, unreliableReceived;
    private static bool reliableAcked, unreliableAcked, remoteDone, remoteDoneAck, completionHandshake;
    private static bool peerClosed;
    private static int waitSeconds = 240;
    private static ProductUserId peer;
    private static P2PInterface p2p;
    private static SocketId socket;
    private static ulong memberNotify, requestNotify, establishedNotify, closedNotify;
    private static readonly byte[] receiveBuffer = new byte[P2PInterface.MAX_PACKET_SIZE];
    private static readonly Dictionary<byte, byte[]> outgoing = new();
    private static readonly Dictionary<byte, byte[]> incoming = new();
    private static string CurrentLobby => ownedLobby ?? joinedLobby;
    private static bool LocalExchangeComplete => relayedConnection && reliableReceived && unreliableReceived && reliableAcked && unreliableAcked;

    private static bool ReadRole()
    {
        role = (Environment.GetEnvironmentVariable("EOS_PROBE_ROLE") ?? "single").ToLowerInvariant();
        if (role != "single" && role != "host" && role != "join") { Emit("configuration", "InvalidRole"); return false; }
        runId = Environment.GetEnvironmentVariable("EOS_PROBE_RUN_ID");
        if (role != "single" && (string.IsNullOrWhiteSpace(runId) || runId.Length > 64 ||
            runId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')))) {
            Emit("configuration", "InvalidRunId");
            return false;
        }
        var waitValue = Environment.GetEnvironmentVariable("EOS_PROBE_WAIT_SECONDS");
        if (!string.IsNullOrEmpty(waitValue) && (!int.TryParse(waitValue, out waitSeconds) || waitSeconds < 30 || waitSeconds > 600)) {
            Emit("configuration", "InvalidWaitSeconds");
            return false;
        }
        return true;
    }

    private static void RunPeerProbe()
    {
        var memberOptions = new AddNotifyLobbyMemberStatusReceivedOptions();
        memberNotify = lobbies.AddNotifyLobbyMemberStatusReceived(ref memberOptions, null,
            (ref LobbyMemberStatusReceivedCallbackInfo info) => {
                if ((string)info.LobbyId == CurrentLobby && info.TargetUserId == peer)
                    Emit("peerLobbyStatus", info.CurrentStatus.ToString());
            });
        if (memberNotify == 0) throw new ProbeFailure("MemberNotificationFailed");
        if (role == "host") CreateAndFindLobby();
        else FindAndJoinLobby();
        WaitForLobbyPeer();
        ConfigureP2P();
        ExchangePackets();
    }

    private static void FindAndJoinLobby()
    {
        var deadline = Stopwatch.StartNew();
        while (string.IsNullOrEmpty(joinedLobby) && deadline.Elapsed < TimeSpan.FromSeconds(waitSeconds)) {
            var searchOptions = new CreateLobbySearchOptions { MaxResults = 4 };
            Expect("createLobbySearch", lobbies.CreateLobbySearch(ref searchOptions, out var search));
            try {
                var parameter = new LobbySearchSetParameterOptions {
                    Parameter = new AttributeData { Key = "IDAS3_PROBE", Value = runId }, ComparisonOp = ComparisonOp.Equal
                };
                Expect("setSearchParameter", search.SetParameter(ref parameter));
                bool done = false;
                var result = Result.UnexpectedError;
                var find = new LobbySearchFindOptions { LocalUserId = user };
                phase = "findLobby";
                search.Find(ref find, null, (ref LobbySearchFindCallbackInfo info) => { result = info.ResultCode; done = true; });
                Wait(() => done, Math.Max(1, Math.Min(30, (int)(waitSeconds - deadline.Elapsed.TotalSeconds))));
                Expect(phase, result);
                var countOptions = new LobbySearchGetSearchResultCountOptions();
                uint count = search.GetSearchResultCount(ref countOptions);
                if (count > 1) throw new ProbeFailure("AmbiguousProbeLobbies");
                if (count == 1) {
                    var copy = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = 0 };
                    Expect("copySearchResult", search.CopySearchResultByIndex(ref copy, out var details));
                    try {
                        var infoOptions = new LobbyDetailsCopyInfoOptions();
                        Expect("readLobby", details.CopyInfo(ref infoOptions, out var info));
                        if (!info.HasValue || info.Value.MaxMembers != 2 || info.Value.LobbyOwnerUserId == user ||
                            (string)info.Value.BucketId != "idas3-eos-probe-v1") throw new ProbeFailure("UnexpectedProbeLobby");
                        var attr = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = "IDAS3_PROBE" };
                        Expect("readMarker", details.CopyAttributeByKey(ref attr, out var attribute));
                        if (!attribute.HasValue || !attribute.Value.Data.HasValue ||
                            (string)attribute.Value.Data.Value.Value.AsUtf8 != runId) throw new ProbeFailure("WrongProbeMarker");
                        done = false;
                        joinInFlight = true;
                        var join = new JoinLobbyOptions { LocalUserId = user, LobbyDetailsHandle = details, PresenceEnabled = false };
                        phase = "joinLobby";
                        lobbies.JoinLobby(ref join, null, (ref JoinLobbyCallbackInfo response) => {
                            result = response.ResultCode;
                            if (result == Result.Success) joinedLobby = response.LobbyId;
                            joinInFlight = false; done = true;
                        });
                        Wait(() => done);
                        Expect(phase, result);
                    } finally { details.Release(); }
                }
            } finally { search.Release(); }
            if (string.IsNullOrEmpty(joinedLobby)) PumpFor(1000);
        }
        phase = "joinLobby";
        if (string.IsNullOrEmpty(joinedLobby)) throw new ProbeFailure("HostRendezvousTimedOut");
    }

    private static void WaitForLobbyPeer()
    {
        phase = "waitForLobbyPeer";
        Emit(phase, "Waiting");
        var deadline = Stopwatch.StartNew();
        while (peer == null && deadline.Elapsed < TimeSpan.FromSeconds(waitSeconds)) {
            if (stopRequested) throw new ProbeFailure("Cancelled");
            var copy = new CopyLobbyDetailsHandleOptions { LocalUserId = user, LobbyId = CurrentLobby };
            var result = lobbies.CopyLobbyDetailsHandle(ref copy, out var details);
            if (result != Result.Success) throw new ProbeFailure("CannotReadCurrentLobby");
            try {
                var countOptions = new LobbyDetailsGetMemberCountOptions();
                uint count = details.GetMemberCount(ref countOptions);
                if (count > 2) throw new ProbeFailure("TooManyLobbyMembers");
                if (count == 2) {
                    bool foundSelf = false;
                    ProductUserId other = null;
                    for (uint i = 0; i < count; i++) {
                        var member = new LobbyDetailsGetMemberByIndexOptions { MemberIndex = i };
                        var id = details.GetMemberByIndex(ref member);
                        if (id == null || !id.IsValid()) throw new ProbeFailure("InvalidMember");
                        if (id == user) foundSelf = true;
                        else if (other == null) other = id;
                        else throw new ProbeFailure("AmbiguousLobbyPeer");
                    }
                    if (!foundSelf || other == null) throw new ProbeFailure("UnexpectedMemberList");
                    peer = other;
                }
            } finally { details.Release(); }
            if (peer == null) PumpFor(250);
        }
        if (peer == null) throw new ProbeFailure("PeerRendezvousTimedOut");
        Emit(phase, "Success", new { members = 2, distinctPeer = true });
    }

    private static void ConfigureP2P()
    {
        p2p = platform.GetP2PInterface();
        var relay = new SetRelayControlOptions { RelayControl = RelayControl.ForceRelays };
        Expect("forceRelays", p2p.SetRelayControl(ref relay));
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(runId))).Substring(0, 20);
        socket = new SocketId { SocketName = "Idas3Probe" + suffix };
        var request = new AddNotifyPeerConnectionRequestOptions { LocalUserId = user, SocketId = socket };
        requestNotify = p2p.AddNotifyPeerConnectionRequest(ref request, null, (ref OnIncomingConnectionRequestInfo info) => {
            if (!MatchesPeer(info.LocalUserId, info.RemoteUserId, info.SocketId)) return;
            var accept = new AcceptConnectionOptions { LocalUserId = user, RemoteUserId = peer, SocketId = socket };
            var result = p2p.AcceptConnection(ref accept);
            if (result != Result.Success) peerError = "AcceptConnection" + result;
        });
        var established = new AddNotifyPeerConnectionEstablishedOptions { LocalUserId = user, SocketId = socket };
        establishedNotify = p2p.AddNotifyPeerConnectionEstablished(ref established, null, (ref OnPeerConnectionEstablishedInfo info) => {
            if (!MatchesPeer(info.LocalUserId, info.RemoteUserId, info.SocketId)) return;
            relayedConnection = info.NetworkType == NetworkConnectionType.RelayedConnection;
            Emit("peerConnectionEstablished", info.NetworkType.ToString());
            if (!relayedConnection) peerError = "ConnectionWasNotRelayed";
        });
        var closed = new AddNotifyPeerConnectionClosedOptions { LocalUserId = user, SocketId = socket };
        closedNotify = p2p.AddNotifyPeerConnectionClosed(ref closed, null, (ref OnRemoteConnectionClosedInfo info) => {
            if (!MatchesPeer(info.LocalUserId, info.RemoteUserId, info.SocketId)) return;
            Emit("peerConnectionClosed", info.Reason.ToString());
            peerClosed = true;
            if (!completionHandshake) peerError = "PeerClosedBeforeCompletion";
        });
        if (requestNotify == 0 || establishedNotify == 0 || closedNotify == 0)
            throw new ProbeFailure("P2PNotificationFailed");
        for (byte kind = 1; kind <= 6; kind++) {
            outgoing[kind] = Payload(role, kind);
            incoming[kind] = Payload(role == "host" ? "join" : "host", kind);
        }
    }

    private static bool MatchesPeer(ProductUserId local, ProductUserId remote, SocketId? id) =>
        local == user && remote == peer && id.HasValue && id.Value.SocketName == socket.SocketName;

    private static byte[] Payload(string sender, byte kind)
    {
        var bytes = new byte[100];
        bytes[0] = (byte)'I'; bytes[1] = (byte)'D'; bytes[2] = 1; bytes[3] = kind;
        var pattern = SHA256.HashData(Encoding.ASCII.GetBytes(runId + ":" + sender + ":" + kind));
        for (int i = 4; i < bytes.Length; i++) bytes[i] = pattern[(i - 4) % pattern.Length];
        return bytes;
    }

    private static void ExchangePackets()
    {
        phase = "relayExchange";
        var clock = Stopwatch.StartNew();
        long nextAccept = 0, nextSend = 0;
        long completedAt = -1;
        while (clock.Elapsed < TimeSpan.FromSeconds(75)) {
            if (stopRequested) throw new ProbeFailure("Cancelled");
            platform.Tick();
            ReceivePackets();
            if (peerError != null) throw new ProbeFailure(peerError);
            long now = clock.ElapsedMilliseconds;
            if (!relayedConnection && now >= nextAccept) {
                var accept = new AcceptConnectionOptions { LocalUserId = user, RemoteUserId = peer, SocketId = socket };
                var result = p2p.AcceptConnection(ref accept);
                if (result != Result.Success) { Emit("acceptPeer", result.ToString()); throw new ProbeFailure("AcceptPeerFailed"); }
                // Queue one authenticated-peer packet to initiate the route before
                // waiting for the established callback. Never auto-accept strangers.
                Send(1, allowDelayedDelivery: true);
                nextAccept = now + 1000;
            }
            if (relayedConnection && !peerClosed && now >= nextSend) {
                if (!reliableAcked) Send(1);
                if (!unreliableAcked) Send(2);
                if (LocalExchangeComplete) {
                    Send(5);
                    if (remoteDone) Send(6);
                }
                nextSend = now + 250;
            }
            if (LocalExchangeComplete && remoteDone && remoteDoneAck && !completionHandshake) {
                completionHandshake = true;
                completedAt = now;
                Emit("completionHandshake", "Success");
            }
            // Both peers keep ticking and acknowledging completion before either closes.
            if (completionHandshake && now - completedAt >= (role == "host" ? 8000 : 5000)) {
                Emit("relayExchange", "Success", new {
                    relayedConnection, reliableReceived, unreliableReceived, reliableAcked, unreliableAcked, payloadBytes = 100
                });
                return;
            }
            Thread.Sleep(10);
        }
        throw new ProbeFailure("RelayExchangeTimedOut");
    }

    private static void Send(byte kind, bool allowDelayedDelivery = false)
    {
        if (completionHandshake && peerClosed) return;
        var options = new SendPacketOptions {
            LocalUserId = user, RemoteUserId = peer, SocketId = socket,
            Channel = Channel(kind), Data = new ArraySegment<byte>(outgoing[kind]),
            Reliability = kind == 2 ? PacketReliability.UnreliableUnordered : PacketReliability.ReliableOrdered,
            AllowDelayedDelivery = allowDelayedDelivery || !relayedConnection, DisableAutoAcceptConnection = true
        };
        var result = p2p.SendPacket(ref options);
        if (result != Result.Success) { Emit("sendPacket", result.ToString()); throw new ProbeFailure("SendPacketFailed"); }
    }

    private static byte Channel(byte kind) => kind == 1 ? (byte)0 : kind == 2 ? (byte)1 : (byte)2;

    private static void ReceivePackets()
    {
        var options = new ReceivePacketOptions { LocalUserId = user, MaxDataSizeBytes = (uint)receiveBuffer.Length };
        ProductUserId sender = null;
        var sourceSocket = new SocketId();
        for (int i = 0; i < 128; i++) {
            var result = p2p.ReceivePacket(ref options, ref sender, ref sourceSocket, out byte channel,
                new ArraySegment<byte>(receiveBuffer), out uint bytesWritten);
            if (result == Result.NotFound) return;
            if (result != Result.Success) { Emit("receivePacket", result.ToString()); throw new ProbeFailure("ReceivePacketFailed"); }
            if (sender != peer || sourceSocket.SocketName != socket.SocketName) continue;
            byte kind = bytesWritten >= 4 ? receiveBuffer[3] : (byte)0;
            if (bytesWritten != 100 || !incoming.TryGetValue(kind, out var expected) || channel != Channel(kind) ||
                !receiveBuffer.AsSpan(0, 100).SequenceEqual(expected)) throw new ProbeFailure("PayloadMismatch");
            switch (kind) {
                case 1:
                    if (!reliableReceived) Emit("receiveReliable", "Success");
                    reliableReceived = true; Send(3); break;
                case 2:
                    if (!unreliableReceived) Emit("receiveUnreliable", "Success");
                    unreliableReceived = true; Send(4); break;
                case 3:
                    if (!reliableAcked) Emit("ackReliable", "Success");
                    reliableAcked = true; break;
                case 4:
                    if (!unreliableAcked) Emit("ackUnreliable", "Success");
                    unreliableAcked = true; break;
                case 5: remoteDone = true; break;
                case 6: remoteDoneAck = true; break;
            }
        }
    }

    private static void CleanupPeer()
    {
        try {
            if (p2p != null) {
                if (requestNotify != 0) p2p.RemoveNotifyPeerConnectionRequest(requestNotify);
                if (establishedNotify != 0) p2p.RemoveNotifyPeerConnectionEstablished(establishedNotify);
                if (closedNotify != 0) p2p.RemoveNotifyPeerConnectionClosed(closedNotify);
                if (peer != null) {
                    var close = new CloseConnectionOptions { LocalUserId = user, RemoteUserId = peer, SocketId = socket };
                    var result = p2p.CloseConnection(ref close);
                    Emit("closePeer", result.ToString());
                    cleanupOk &= result == Result.Success || result == Result.NotFound;
                }
            }
            if (lobbies != null && memberNotify != 0) lobbies.RemoveNotifyLobbyMemberStatusReceived(memberNotify);
        } catch (Exception ex) { cleanupOk = false; Emit("removePeerNotifications", ex.GetType().Name); }
        if (platform != null && joinInFlight) {
            try { phase = "settlePendingLobbyJoin"; Wait(() => !joinInFlight, 30, cleanup: true); }
            catch (Exception ex) { cleanupOk = false; Emit(phase, ex is ProbeFailure failure ? failure.Code : ex.GetType().Name); }
        }
        if (platform != null && lobbies != null && !string.IsNullOrEmpty(joinedLobby)) {
            try {
                bool done = false;
                var result = Result.UnexpectedError;
                var leave = new LeaveLobbyOptions { LocalUserId = user, LobbyId = joinedLobby };
                lobbies.LeaveLobby(ref leave, null, (ref LeaveLobbyCallbackInfo info) => { result = info.ResultCode; done = true; });
                phase = "leaveLobby";
                Wait(() => done, 15, cleanup: true);
                Emit(phase, result.ToString());
                cleanupOk &= result == Result.Success || result == Result.NotFound;
            } catch (Exception ex) { cleanupOk = false; Emit("leaveLobby", ex is ProbeFailure failure ? failure.Code : ex.GetType().Name); }
        }
    }
}
