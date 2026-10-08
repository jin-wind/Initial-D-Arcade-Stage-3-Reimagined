using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport.Utilities;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Core.Environments;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using TransportError = Unity.Networking.Transport.Error;

namespace Idas3.Multiplayer
{
    internal sealed class Idas3RelayRoute
    {
        internal RelayServerData Server;
        internal string Code, LocalId;
    }

    internal interface IIdas3RelayService
    {
        bool Configured { get; }
        Task<Idas3RelayRoute> Host();
        Task<Idas3RelayRoute> Join(string code);
    }

    // Uses the Relay API in the Unity 6 Multiplayer Services SDK, without
    // introducing NGO or replacing the game's native rollback simulation.
    internal sealed class Idas3UnityRelayService : IIdas3RelayService
    {
        [Serializable] sealed class Config { public string environment = "production"; }
        static Task authentication;
        public bool Configured => Guid.TryParse(Application.cloudProjectId, out var id) && id != Guid.Empty;

        static async Task Authenticate()
        {
            if (UnityServices.State != ServicesInitializationState.Initialized) {
                var asset = Resources.Load<TextAsset>("Idas3UnityServices");
                var config = asset == null ? new Config() : JsonUtility.FromJson<Config>(asset.text);
                if (config == null || string.IsNullOrWhiteSpace(config.environment))
                    throw new InvalidOperationException("Unity Services environment is missing.");
                await UnityServices.InitializeAsync(new InitializationOptions().SetEnvironmentName(config.environment));
            }
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        static Task EnsureAuthentication()
        {
            // Service state is shared by the player. Leaving a room cancels
            // that operation, but does not sign out other service consumers.
            if (authentication == null || authentication.IsCompleted) authentication = Authenticate();
            return authentication;
        }

        public async Task<Idas3RelayRoute> Host()
        {
            await EnsureAuthentication();
            var allocation = await RelayService.Instance.CreateAllocationAsync(1);
            var code = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            return new Idas3RelayRoute { Server = AllocationUtils.ToRelayServerData(allocation, "dtls"),
                Code = code, LocalId = AuthenticationService.Instance.PlayerId };
        }

        public async Task<Idas3RelayRoute> Join(string code)
        {
            await EnsureAuthentication();
            var allocation = await RelayService.Instance.JoinAllocationAsync(code);
            return new Idas3RelayRoute { Server = AllocationUtils.ToRelayServerData(allocation, "dtls"),
                Code = code, LocalId = AuthenticationService.Instance.PlayerId };
        }
    }

    // Only the single peer admitted to this authenticated Relay allocation
    // can emit session messages. The session still validates the build, role,
    // nonce, garage and every game message before starting the native race.
    public sealed class Idas3UnityRelayTransport : IIdas3Transport, IIdas3AsyncTransport
    {
        internal const int MaxMessage = 4096, MaxQueuedBytes = 131072;
        const double ConnectTimeout = 30;
        static readonly Idas3Room[] NoRooms = Array.Empty<Idas3Room>();
        readonly IIdas3RelayService service;
        readonly Func<double> now;
        readonly bool loopbackCheck;
        readonly Queue<byte[]> reliableQueue = new Queue<byte[]>();
        NetworkDriver driver;
        NetworkConnection peer;
        NetworkPipeline reliablePipeline, unreliablePipeline;
        int queuedBytes, generation;
        bool disposed, hosting, bound;
        double startedAt;
        string pendingCode = "";
        public string Kind => "Unity Relay";
        public bool Available { get; private set; }
        public bool Connected { get; private set; }
        public bool IsHost { get; private set; }
        public bool IsBusy { get; private set; }
        public string LocalId { get; private set; } = "";
        public string LocalName => string.IsNullOrEmpty(LocalId) ? "Driver" : "Driver " + LocalId.Substring(0, Math.Min(4, LocalId.Length));
        public string RemoteId => Connected ? "relay-peer" : "";
        public string RemoteName => Connected ? "Other driver" : "";
        public string RoomCode { get; private set; } = "";
        public string Status { get; private set; } = "Internet play uses a room code.";
        public IReadOnlyList<Idas3Room> Rooms => NoRooms;
        public long BytesSent { get; private set; }
        public long BytesReceived { get; private set; }
        public long UnreliablePacketsDropped { get; private set; }
        public event Action<byte[]> Message;
        public event Action PeerChanged;
        public event Action<string> Error;
#if UNITY_EDITOR
        internal NetworkEndpoint CheckEndpoint => driver.IsCreated ? driver.GetLocalEndpoint() : default;
#endif

        public Idas3UnityRelayTransport() : this(new Idas3UnityRelayService()) { }
        internal Idas3UnityRelayTransport(IIdas3RelayService service, bool loopbackCheck = false, Func<double> now = null)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.loopbackCheck = loopbackCheck;
            this.now = now ?? (() => Time.realtimeSinceStartupAsDouble);
        }

        public bool Initialize()
        {
            if (disposed) return false;
            Available = service.Configured;
            if (!IsBusy && !Connected && string.IsNullOrEmpty(RoomCode))
                Status = Available ? "Internet ready. Host a battle or enter your friend's room code." :
                    "Internet play is not configured in this build. Use LAN or a build linked to Unity Relay.";
            return Available;
        }

        internal static bool TryCode(string input, out string code)
        {
            code = (input ?? "").Trim().ToUpperInvariant();
            // Relay codes are case insensitive. Keep input bounded without
            // baking the provider's current code length into our protocol.
            if (code.Length < 3 || code.Length > 16) return false;
            foreach (char c in code) if (!(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9')) return false;
            return true;
        }

        public void Host(string roomName) => Start(true, "");
        public void Join(string roomCode)
        {
            if (disposed || IsBusy || Connected || !string.IsNullOrEmpty(RoomCode)) return;
            if (!TryCode(roomCode, out var code)) { Fail("Enter the room code shared by your friend (letters and numbers)."); return; }
            Start(false, code);
        }
        void Start(bool host, string code)
        {
            if (disposed || IsBusy || Connected || !string.IsNullOrEmpty(RoomCode)) return;
            if (!Initialize()) { Error?.Invoke(Status); return; }
            IsBusy = true; hosting = host; startedAt = now();
            Status = host ? "Signing in and creating an internet room…" : "Signing in and joining the internet room…";
            _ = Allocate(host, code, ++generation);
        }

        async Task Allocate(bool host, string code, int operation)
        {
            try {
                var route = await (host ? service.Host() : service.Join(code));
                // HTTP requests may complete after Leave, timeout or Dispose.
                // They must never recreate a cancelled room or its sockets.
                if (disposed || operation != generation) return;
                if (now() - startedAt > ConnectTimeout) { Fail("Internet connection timed out. Check your network and try again."); return; }
                if (route == null || !TryCode(route.Code, out pendingCode) || string.IsNullOrWhiteSpace(route.LocalId))
                    throw new InvalidOperationException("Relay returned incomplete connection information.");
                LocalId = route.LocalId;
                using (var settings = Settings()) {
                    var configured = settings;
                    if (!loopbackCheck) configured.WithRelayParameters(ref route.Server);
                    driver = NetworkDriver.Create(configured);
                }
                // Pipeline order and limits must match on both platforms.
                reliablePipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage), typeof(ReliableSequencedPipelineStage));
                unreliablePipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage));
                if (driver.Bind(NetworkEndpoint.AnyIpv4) != 0)
                    throw new InvalidOperationException("Could not open the internet transport.");
                if (host) {
                    if (driver.Listen() != 0) throw new InvalidOperationException("Could not listen for the second driver.");
                } else {
                    peer = driver.Connect(route.Server.Endpoint);
                    if (!peer.IsCreated) throw new InvalidOperationException("Could not connect to the room.");
                }
                Status = "Connecting to Unity Relay…";
            } catch (Exception e) {
                if (!disposed && operation == generation) Fail(ConnectionError(e));
            }
        }

        static NetworkSettings Settings()
        {
            var settings = new NetworkSettings(Allocator.Temp);
            settings.WithNetworkConfigParameters(connectTimeoutMS: 1000, maxConnectAttempts: 20,
                disconnectTimeoutMS: 10000, heartbeatTimeoutMS: 500, receiveQueueCapacity: 128, sendQueueCapacity: 128);
            settings.WithFragmentationStageParameters(payloadCapacity: MaxMessage);
            settings.WithReliableStageParameters(windowSize: 64);
            return settings;
        }

        static string ConnectionError(Exception error)
        {
            // Do not dump request bodies, tokens or allocation keys to the UI.
            if (error is RelayServiceException relay) {
                switch (relay.Reason) {
                    case RelayExceptionReason.JoinCodeNotFound: return "Room code not found. Ask your friend to create a new room.";
                    case RelayExceptionReason.Conflict: return "This room is unavailable or already has two drivers. Ask your friend for a new code.";
                }
                return "Unity Relay could not connect. Check your network, service configuration and usage allowance.";
            }
            if (error is RequestFailedException) return "Unity sign-in failed. Check your network and the game's Unity Services configuration.";
            return "Internet connection failed. Check your network and try again.";
        }

        public void Browse()
        {
            if (!Initialize() || IsBusy || !string.IsNullOrEmpty(RoomCode)) return;
            Status = "Share a room code to play with friends over Wi-Fi or mobile data.";
        }

        public void Send(byte[] data, bool reliable)
        {
            if (!Connected || disposed || data == null) return;
            if (data.Length == 0 || data.Length > MaxMessage) { Fail("Network packet exceeds the game limit."); return; }
            if (reliable) {
                if (queuedBytes + data.Length > MaxQueuedBytes) { Fail("The other driver stopped receiving data."); return; }
                // Retain the message boundary and ownership even if the caller
                // reuses its buffer before the next driver update.
                reliableQueue.Enqueue((byte[])data.Clone()); queuedBytes += data.Length;
            } else if (!TrySend(data, unreliablePipeline)) ++UnreliablePacketsDropped;
        }

        bool TrySend(byte[] data, NetworkPipeline pipeline)
        {
            int result = driver.BeginSend(pipeline, peer, out var writer, data.Length);
            if (result == (int)TransportError.StatusCode.NetworkSendQueueFull) return false;
            if (result != 0) { Fail("Internet connection could not send data."); return false; }
            for (int i = 0; i < data.Length; ++i) writer.WriteByte(data[i]);
            result = driver.EndSend(writer);
            if (result == (int)TransportError.StatusCode.NetworkSendQueueFull) return false;
            if (result < 0) { Fail("Internet connection could not send data."); return false; }
            BytesSent += data.Length;
            return true;
        }

        void FlushReliable()
        {
            int count = 0;
            while (Connected && driver.IsCreated && reliableQueue.Count > 0 && ++count <= 64) {
                var packet = reliableQueue.Peek();
                if (!TrySend(packet, reliablePipeline)) break;
                if (!Connected || !driver.IsCreated) break;
                reliableQueue.Dequeue(); queuedBytes -= packet.Length;
            }
        }

        public void Poll()
        {
            if (disposed) return;
            if (IsBusy && now() - startedAt > ConnectTimeout) { Fail("Internet connection timed out. Check your network and try again."); return; }
            if (!driver.IsCreated) return;
            driver.ScheduleUpdate().Complete();
            var relay = loopbackCheck ? RelayConnectionStatus.Established : driver.GetRelayConnectionStatus();
            if (relay == RelayConnectionStatus.AllocationInvalid) { Fail("The internet room expired. Create a new room and share its code."); return; }
            if (!bound && relay == RelayConnectionStatus.Established) {
                bound = true; RoomCode = pendingCode;
                if (hosting) {
                    IsHost = true; IsBusy = false; Status = "Room open. Share the room code with your friend.";
                    PeerChanged?.Invoke();
                    if (!driver.IsCreated) return;
                }
            }
            if (hosting) {
                for (int i = 0; i < 16; ++i) {
                    var incoming = driver.Accept();
                    if (!incoming.IsCreated) break;
                    if (peer.IsCreated) driver.Disconnect(incoming);
                    else { peer = incoming; Admit(); }
                    if (!driver.IsCreated) return;
                }
            }
            for (int i = 0; i < 128 && driver.IsCreated; ++i) {
                var type = driver.PopEvent(out var source, out var reader, out var pipeline);
                if (type == NetworkEvent.Type.Empty) break;
                if (source != peer) continue;
                if (type == NetworkEvent.Type.Connect) Admit();
                else if (type == NetworkEvent.Type.Disconnect) { Fail("The other driver disconnected. Create or join a new room."); return; }
                else if (type == NetworkEvent.Type.Data && Connected) {
                    if (reader.Length <= 0 || reader.Length > MaxMessage ||
                        (pipeline != reliablePipeline && pipeline != unreliablePipeline)) {
                        Fail("Rejected an invalid internet packet."); return;
                    }
                    var packet = new byte[reader.Length];
                    for (int b = 0; b < packet.Length; ++b) packet[b] = reader.ReadByte();
                    BytesReceived += packet.Length;
                    Message?.Invoke(packet);
                }
            }
            FlushReliable();
        }

        void Admit()
        {
            if (Connected) return;
            Connected = true; IsBusy = false; RoomCode = pendingCode;
            IsHost = hosting; Status = "Driver connected through Unity Relay.";
            PeerChanged?.Invoke();
        }

        void Fail(string message)
        {
            Close(false); Status = message; Error?.Invoke(message);
        }

        public void Leave()
        {
            Close(true);
            Status = Available ? "Internet ready. Host a new room or enter a room code." : "Internet play is not configured in this build.";
        }

        void Close(bool flush)
        {
            ++generation;
            bool hadRoom = Connected || !string.IsNullOrEmpty(RoomCode);
            // Send an already queued session Leave before closing the route.
            if (driver.IsCreated) {
                if (flush) FlushReliable();
                if (driver.IsCreated) {
                    driver.ScheduleFlushSend().Complete();
                    if (peer.IsCreated) driver.Disconnect(peer);
                    driver.ScheduleUpdate().Complete();
                    driver.Dispose(); driver = default;
                }
            }
            peer = default; reliableQueue.Clear(); queuedBytes = 0;
            Connected = IsHost = IsBusy = hosting = bound = false;
            RoomCode = pendingCode = "";
            if (hadRoom) PeerChanged?.Invoke();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Available = false; Close(false);
        }
    }
}
