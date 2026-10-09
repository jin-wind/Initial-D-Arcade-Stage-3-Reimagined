#if IDAS3_EOS && UNITY_ANDROID
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.P2P;
using UnityEngine;
using static Idas3.Multiplayer.Idas3EosRuntime;

namespace Idas3.Multiplayer
{
    // EOS supplies identity, lobby discovery and relays. Session handshake,
    // cars, race authority, rollback and result validation stay in the game.
    public sealed class Idas3EosTransport : IIdas3Transport, IIdas3AsyncTransport
    {
        const string Bucket = "idas3-internet-v1", CodeAttribute = "IDAS3_ROOM";
        const int Header = 12, Chunk = 1158, MaxMessage = 4096, MaxQueued = 131072;
        static readonly Idas3Room[] EmptyRooms = Array.Empty<Idas3Room>();
        readonly Queue<byte[]> sendQueue = new Queue<byte[]>();
        readonly Dictionary<uint, Assembly> partial = new Dictionary<uint, Assembly>();
        readonly byte[] buffer = new byte[1170];
        Idas3EosRuntime runtime;
        LobbyInterface lobbies;
        P2PInterface p2p;
        ProductUserId user, peer;
        SocketId socket;
        string lobby = "", pendingFailure;
        int generation, queuedBytes;
        uint messageId;
        ulong requested, established, closed;
        bool disposed, relayReady, acceptPending;
        double startedAt, nextMembers, nextAccept;
        public string Kind => "EOS Relay";
        public bool Available { get; private set; }
        public bool Connected { get; private set; }
        public bool IsHost { get; private set; }
        public bool IsBusy { get; private set; }
        public string LocalId => user?.ToString() ?? "";
        public string LocalName => "Driver";
        public string RemoteId => peer?.ToString() ?? "";
        public string RemoteName => "Other driver";
        public string RoomCode { get; private set; } = "";
        public string Status { get; private set; } = "Host an internet room or enter your friend's code.";
        public IReadOnlyList<Idas3Room> Rooms => EmptyRooms;
        public event Action<byte[]> Message;
        public event Action PeerChanged;
        public event Action<string> Error;
        static double Now => Time.realtimeSinceStartupAsDouble;
        bool Current(int operation) => !disposed && generation == operation;

        public bool Initialize()
        {
            Available = !disposed && Configured;
            if (!Available) Status = "Internet play is not configured in this build. LAN is available.";
            return Available;
        }
        public void Browse() { if (Initialize()) Status = "Share a room code to play over Wi-Fi or mobile data."; }
        public void Host(string roomName) => Start(true, "");
        public void Join(string roomCode)
        {
            string code = (roomCode ?? "").Trim().ToUpperInvariant();
            if (code.Length != 8) { Error?.Invoke("Enter the 8-character room code shared by your friend."); return; }
            foreach (char c in code) if ("ABCDEFGHJKLMNPQRSTUVWXYZ23456789".IndexOf(c) < 0) {
                Error?.Invoke("Room codes contain capital letters and numbers 2 to 9."); return;
            }
            Start(false, code);
        }
        void Start(bool host, string code)
        {
            if (disposed || IsBusy || lobby.Length != 0 || Connected) return;
            if (!Initialize()) { Error?.Invoke(Status); return; }
            IsBusy = true; startedAt = Now;
            Status = host ? "Signing in and creating a room…" : "Signing in and joining your friend…";
            _ = Open(host, code, ++generation);
        }

        async Task Open(bool host, string code, int operation)
        {
            try {
                runtime = Get();
                await runtime.Authenticate();
                if (!Current(operation)) return;
                user = runtime.User; lobbies = runtime.Platform.GetLobbyInterface(); p2p = runtime.Platform.GetP2PInterface();
                if (host) await CreateRoom(operation); else await JoinRoom(code, operation);
                if (!Current(operation)) return;
                ConfigurePeer(operation);
                IsBusy = !host; IsHost = host;
                Status = host ? "Room open. Share the code with your friend." : "Connecting through EOS relay…";
                nextMembers = 0;
                Debug.Log("IDAS3_EOS " + (host ? "room created" : "room joined"));
                if (host) PeerChanged?.Invoke();
            } catch (Exception e) { if (Current(operation)) Fail(SafeError(e)); }
        }

        async Task CreateRoom(int operation)
        {
            var api = lobbies;
            var create = new CreateLobbyOptions {
                LocalUserId = user, MaxLobbyMembers = 2, PermissionLevel = LobbyPermissionLevel.Publicadvertised,
                PresenceEnabled = false, AllowInvites = false, BucketId = Bucket,
                DisableHostMigration = true, EnableRTCRoom = false, CrossplayOptOut = false
            };
            var created = await Callback<CreateLobbyCallbackInfo>(done => api.CreateLobby(ref create, null,
                (ref CreateLobbyCallbackInfo info) => {
                    if (info.ResultCode == Result.Success) {
                        string id = info.LobbyId;
                        if (Current(operation)) { lobby = id; IsHost = true; }
                        else DestroyRoom(api, user, id);
                    }
                    done(info);
                }));
            Check(created.ResultCode, "create room");
            if (!Current(operation)) return;
            string code = NewCode();
            var modifyOptions = new UpdateLobbyModificationOptions { LocalUserId = user, LobbyId = lobby };
            Check(api.UpdateLobbyModification(ref modifyOptions, out var modification), "edit room");
            try {
                var attr = new LobbyModificationAddAttributeOptions {
                    Attribute = new AttributeData { Key = CodeAttribute, Value = code }, Visibility = LobbyAttributeVisibility.Public
                };
                Check(modification.AddAttribute(ref attr), "room code");
                var update = new UpdateLobbyOptions { LobbyModificationHandle = modification };
                var changed = await Callback<UpdateLobbyCallbackInfo>(done => api.UpdateLobby(ref update, null,
                    (ref UpdateLobbyCallbackInfo info) => done(info)));
                Check(changed.ResultCode, "publish room");
                if (Current(operation)) RoomCode = code;
            } finally { modification.Release(); }
        }

        async Task JoinRoom(string code, int operation)
        {
            var api = lobbies;
            var local = user;
            var create = new CreateLobbySearchOptions { MaxResults = 3 };
            Check(api.CreateLobbySearch(ref create, out var search), "search room");
            try {
                var filter = new LobbySearchSetParameterOptions {
                    Parameter = new AttributeData { Key = CodeAttribute, Value = code }, ComparisonOp = ComparisonOp.Equal
                };
                Check(search.SetParameter(ref filter), "room filter");
                var find = new LobbySearchFindOptions { LocalUserId = local };
                var found = await Callback<LobbySearchFindCallbackInfo>(done => search.Find(ref find, null,
                    (ref LobbySearchFindCallbackInfo info) => done(info)));
                Check(found.ResultCode, "find room");
                if (!Current(operation)) return;
                var count = new LobbySearchGetSearchResultCountOptions();
                uint matches = search.GetSearchResultCount(ref count);
                if (matches != 1) throw new InvalidOperationException("Room not found or unavailable. Ask your friend for a new code.");
                var copy = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = 0 };
                Check(search.CopySearchResultByIndex(ref copy, out var details), "read room");
                try {
                    var read = new LobbyDetailsCopyInfoOptions();
                    Check(details.CopyInfo(ref read, out var info), "room details");
                    if (!info.HasValue || (string)info.Value.BucketId != Bucket || info.Value.MaxMembers != 2 ||
                        info.Value.LobbyOwnerUserId == local) throw new InvalidOperationException("This room is not available.");
                    var join = new JoinLobbyOptions { LocalUserId = local, LobbyDetailsHandle = details, PresenceEnabled = false };
                    var joined = await Callback<JoinLobbyCallbackInfo>(done => api.JoinLobby(ref join, null,
                        (ref JoinLobbyCallbackInfo result) => {
                            if (result.ResultCode == Result.Success) {
                                string id = result.LobbyId;
                                if (Current(operation)) lobby = id; else LeaveJoinedRoom(api, local, id);
                            }
                            done(result);
                        }));
                    Check(joined.ResultCode, "join room");
                    if (Current(operation)) RoomCode = code;
                } finally { details.Release(); }
            } finally { search.Release(); }
        }

        static string NewCode()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = new byte[8]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var chars = new char[8]; for (int i = 0; i < 8; ++i) chars[i] = alphabet[bytes[i] & 31];
            return new string(chars);
        }

        void ConfigurePeer(int operation)
        {
            var relay = new SetRelayControlOptions { RelayControl = RelayControl.ForceRelays };
            Check(p2p.SetRelayControl(ref relay), "relay mode");
            using (var hash = SHA256.Create()) {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(lobby));
                socket = new SocketId { SocketName = "Idas3" + BitConverter.ToString(bytes, 0, 10).Replace("-", "") };
            }
            var request = new AddNotifyPeerConnectionRequestOptions { LocalUserId = user, SocketId = socket };
            requested = p2p.AddNotifyPeerConnectionRequest(ref request, null, (ref OnIncomingConnectionRequestInfo info) => {
                if (Current(operation) && Matches(info.LocalUserId, info.RemoteUserId, info.SocketId)) acceptPending = true;
            });
            var connected = new AddNotifyPeerConnectionEstablishedOptions { LocalUserId = user, SocketId = socket };
            established = p2p.AddNotifyPeerConnectionEstablished(ref connected, null, (ref OnPeerConnectionEstablishedInfo info) => {
                if (!Current(operation) || !Matches(info.LocalUserId, info.RemoteUserId, info.SocketId)) return;
                if (info.NetworkType != NetworkConnectionType.RelayedConnection) pendingFailure = "The internet relay was not established.";
                else { relayReady = true; Debug.Log("IDAS3_EOS RelayedConnection"); }
            });
            var disconnect = new AddNotifyPeerConnectionClosedOptions { LocalUserId = user, SocketId = socket };
            closed = p2p.AddNotifyPeerConnectionClosed(ref disconnect, null, (ref OnRemoteConnectionClosedInfo info) => {
                if (Current(operation) && Matches(info.LocalUserId, info.RemoteUserId, info.SocketId))
                    pendingFailure = "The other driver disconnected. Create or join a new room.";
            });
            if (requested == 0 || established == 0 || closed == 0) throw new InvalidOperationException("EOS connection setup failed.");
        }
        bool Matches(ProductUserId local, ProductUserId remote, SocketId? id) => peer != null &&
            local == user && remote == peer && id.HasValue && id.Value.SocketName == socket.SocketName;

        void Members()
        {
            var copy = new CopyLobbyDetailsHandleOptions { LocalUserId = user, LobbyId = lobby };
            Check(lobbies.CopyLobbyDetailsHandle(ref copy, out var details), "room members");
            try {
                var countOption = new LobbyDetailsGetMemberCountOptions();
                uint count = details.GetMemberCount(ref countOption);
                if (count > 2 || count == 0) throw new InvalidOperationException("Invalid room members.");
                bool self = false;
                ProductUserId other = null;
                for (uint index = 0; index < count; ++index) {
                    var member = new LobbyDetailsGetMemberByIndexOptions { MemberIndex = index };
                    var id = details.GetMemberByIndex(ref member);
                    if (id == user) self = true; else other = id;
                }
                if (!self) throw new InvalidOperationException("You left the internet room.");
                if (peer != null && other != peer) throw new InvalidOperationException("The other driver left the room.");
                if (other != null && other.IsValid() && peer == null) {
                    peer = other; startedAt = Now; IsBusy = true; nextAccept = 0;
                    Status = "Connecting to the other driver through EOS relay…";
                }
            } finally { details.Release(); }
        }

        public void Poll()
        {
            if (disposed) return;
            try {
                if (pendingFailure != null) { Fail(pendingFailure); return; }
                if (IsBusy && Now - startedAt > 30) { Fail("Internet connection timed out. Please try again."); return; }
                if (lobby.Length == 0 || requested == 0) return;
                if (Now >= nextMembers) { nextMembers = Now + 0.5; Members(); }
                if (peer == null) return;
                if (!relayReady && (acceptPending || Now >= nextAccept)) {
                    var accept = new AcceptConnectionOptions { LocalUserId = user, RemoteUserId = peer, SocketId = socket };
                    Check(p2p.AcceptConnection(ref accept), "accept peer");
                    // Send a transport-only packet to establish the route. Race
                    // messages wait for the native RelayedConnection callback.
                    var hello = new byte[] { 0x49, 0x44, 0x45, 1 };
                    SendRaw(hello, 2, true, true);
                    nextAccept = Now + 1; acceptPending = false;
                }
                if (relayReady && !Connected) {
                    Connected = true; IsBusy = false; Status = "Connected through EOS relay.";
                    PeerChanged?.Invoke();
                    if (!Connected) return;
                }
                if (!Connected) return;
                Flush();
                Receive();
            } catch (Exception e) { Fail(SafeError(e)); }
        }

        public void Send(byte[] data, bool reliable)
        {
            if (!Connected || disposed || data == null) return;
            if (data.Length == 0 || data.Length > MaxMessage) { Fail("Network packet exceeds the game limit."); return; }
            int count = (data.Length + Chunk - 1) / Chunk;
            if (reliable && queuedBytes + data.Length + Header * count > MaxQueued) {
                Fail("The other driver stopped receiving data."); return;
            }
            uint id = ++messageId;
            for (int index = 0; index < count; ++index) {
                int size = Math.Min(Chunk, data.Length - index * Chunk);
                var packet = new byte[Header + size];
                packet[0] = 0x49; packet[1] = 0x44; packet[2] = 1; packet[3] = reliable ? (byte)0 : (byte)1;
                for (int b = 0; b < 4; ++b) packet[4+b] = (byte)(id >> (8*b));
                packet[8] = (byte)data.Length; packet[9] = (byte)(data.Length >> 8);
                packet[10] = (byte)index; packet[11] = (byte)count;
                Buffer.BlockCopy(data, index * Chunk, packet, Header, size);
                if (reliable) { sendQueue.Enqueue(packet); queuedBytes += packet.Length; }
                else if (!SendRaw(packet, 1, false, false)) break;
            }
        }

        bool SendRaw(byte[] bytes, byte channel, bool reliable, bool delayed)
        {
            var send = new SendPacketOptions {
                LocalUserId = user, RemoteUserId = peer, SocketId = socket, Channel = channel,
                Data = new ArraySegment<byte>(bytes), AllowDelayedDelivery = delayed,
                DisableAutoAcceptConnection = true,
                Reliability = reliable ? PacketReliability.ReliableOrdered : PacketReliability.UnreliableUnordered
            };
            Result result = p2p.SendPacket(ref send);
            if (result == Result.LimitExceeded) return false;
            Check(result, "send packet"); return true;
        }
        void Flush()
        {
            for (int i = 0; Connected && sendQueue.Count > 0 && i < 64; ++i) {
                byte[] data = sendQueue.Peek(); if (!SendRaw(data, 0, true, false)) return;
                sendQueue.Dequeue(); queuedBytes -= data.Length;
            }
        }

        sealed class Assembly
        {
            internal byte[] Data;
            internal int Mask, Count;
            internal byte Channel;
            internal double Created;
        }
        void Receive()
        {
            var options = new ReceivePacketOptions { LocalUserId = user, MaxDataSizeBytes = 1170 };
            ProductUserId from = null; var fromSocket = new SocketId();
            for (int i = 0; Connected && i < 256; ++i) {
                Result result = p2p.ReceivePacket(ref options, ref from, ref fromSocket, out byte channel,
                    new ArraySegment<byte>(buffer), out uint bytes);
                if (result == Result.NotFound) return;
                Check(result, "receive packet");
                if (from != peer || fromSocket.SocketName != socket.SocketName) continue;
                if (channel == 2) continue;
                if (channel > 1 || bytes < Header || buffer[0] != 0x49 || buffer[1] != 0x44 ||
                    buffer[2] != 1 || buffer[3] != channel) throw new InvalidOperationException("Invalid internet packet.");
                uint id = 0; for (int b = 0; b < 4; ++b) id |= (uint)buffer[4+b] << (8*b);
                int total = buffer[8] | buffer[9] << 8, index = buffer[10], count = buffer[11];
                if (total <= 0 || total > MaxMessage || count != (total + Chunk - 1)/Chunk || index >= count ||
                    bytes != Header + Math.Min(Chunk, total - index * Chunk)) throw new InvalidOperationException("Invalid internet fragment.");
                if (!partial.TryGetValue(id, out var assembly)) {
                    var expired = new List<uint>();
                    foreach (var entry in partial) if (entry.Value.Channel == 1 && Now - entry.Value.Created > 2) expired.Add(entry.Key);
                    foreach (uint old in expired) partial.Remove(old);
                    if (partial.Count >= 16) {
                        if (channel == 1) continue;
                        throw new InvalidOperationException("Incomplete internet messages exceeded the limit.");
                    }
                    assembly = new Assembly { Data = new byte[total], Count = count, Channel = channel, Created = Now };
                    partial.Add(id, assembly);
                }
                if (assembly.Data.Length != total || assembly.Count != count || assembly.Channel != channel)
                    throw new InvalidOperationException("Inconsistent internet fragments.");
                Buffer.BlockCopy(buffer, Header, assembly.Data, index * Chunk, (int)bytes - Header);
                assembly.Mask |= 1 << index;
                if (assembly.Mask == (1 << count) - 1) { partial.Remove(id); Message?.Invoke(assembly.Data); }
            }
        }

        static string SafeError(Exception error)
        {
            if (error is TimeoutException) return "Internet request timed out. Check your network and try again.";
            if (error is InvalidOperationException) return error.Message;
            return "Internet connection failed. Check your network and try again.";
        }
        void Fail(string reason) { Close(false); Status = reason; Debug.LogWarning("IDAS3_EOS " + reason); Error?.Invoke(reason); }
        public void Leave() { Close(true); Status = "Internet ready. Create a new room or enter a room code."; }
        void Close(bool flush)
        {
            ++generation;
            bool hadRoom = Connected || lobby.Length != 0;
            if (p2p != null) {
                if (flush) { try { Flush(); } catch { } }
                if (requested != 0) p2p.RemoveNotifyPeerConnectionRequest(requested);
                if (established != 0) p2p.RemoveNotifyPeerConnectionEstablished(established);
                if (closed != 0) p2p.RemoveNotifyPeerConnectionClosed(closed);
                if (peer != null) {
                    var close = new CloseConnectionOptions { LocalUserId = user, RemoteUserId = peer, SocketId = socket };
                    p2p.CloseConnection(ref close);
                }
            }
            if (lobbies != null && lobby.Length != 0) {
                if (IsHost) DestroyRoom(lobbies, user, lobby); else LeaveJoinedRoom(lobbies, user, lobby);
            }
            lobby = RoomCode = ""; pendingFailure = null; peer = null;
            requested = established = closed = 0;
            Connected = IsBusy = IsHost = relayReady = acceptPending = false;
            sendQueue.Clear(); queuedBytes = 0; partial.Clear();
            if (hadRoom) PeerChanged?.Invoke();
        }
        static void DestroyRoom(LobbyInterface api, ProductUserId local, string id)
        {
            var destroy = new DestroyLobbyOptions { LocalUserId = local, LobbyId = id };
            api.DestroyLobby(ref destroy, null, (ref DestroyLobbyCallbackInfo info) => {
                Debug.Log("IDAS3_EOS destroy room: " + info.ResultCode);
            });
        }
        static void LeaveJoinedRoom(LobbyInterface api, ProductUserId local, string id)
        {
            var leave = new LeaveLobbyOptions { LocalUserId = local, LobbyId = id };
            api.LeaveLobby(ref leave, null, (ref LeaveLobbyCallbackInfo info) => {
                Debug.Log("IDAS3_EOS leave room: " + info.ResultCode);
            });
        }
        public void Dispose() { if (disposed) return; disposed = true; Available = false; Close(false); }
    }
}
#endif
