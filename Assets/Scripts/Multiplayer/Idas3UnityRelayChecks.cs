#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;

namespace Idas3.Multiplayer
{
    // Real UTP UDP/pipeline checks with a substituted allocation service.
    // No Unity account, allocation, billing or ordinary player save is used.
    public static class Idas3UnityRelayChecks
    {
        sealed class Allocator : IIdas3RelayService
        {
            public bool Configured { get; set; } = true;
            public int Requests;
            public Func<Task<Idas3RelayRoute>> Request;
            public Task<Idas3RelayRoute> Host() { ++Requests; return Request(); }
            public Task<Idas3RelayRoute> Join(string code) { ++Requests; return Request(); }
        }

        static Idas3RelayRoute Route(ushort port = 0, string code = "ABC123") => new Idas3RelayRoute {
            Code = code, LocalId = "isolated-test-driver",
            Server = new RelayServerData { Endpoint = NetworkEndpoint.LoopbackIpv4.WithPort(port) }
        };

        public static int RunSelfTests()
        {
            int checks = 0;
            void Check(bool condition, string name) {
                if (!condition) throw new InvalidOperationException("Unity Relay check failed: " + name);
                ++checks;
            }
            Check(Idas3UnityRelayTransport.TryCode("  abc123  ", out var normalized) && normalized == "ABC123", "code normalization");
            foreach (string invalid in new[] { "", "ab", "ABC:123", "<ROOM>", "A B C", new string('A', 17), "房間碼" })
                Check(!Idas3UnityRelayTransport.TryCode(invalid, out _), "invalid code rejected");

            var disabled = new Allocator { Configured = false, Request = () => Task.FromResult(Route()) };
            using (var transport = new Idas3UnityRelayTransport(disabled, true)) {
                int errors = 0; transport.Error += _ => ++errors;
                Check(!transport.Initialize(), "unlinked project unavailable"); transport.Host("test");
                Check(disabled.Requests == 0 && errors == 1 && !transport.IsBusy, "unlinked project makes no service request");
            }

            var first = new TaskCompletionSource<Idas3RelayRoute>();
            var second = new TaskCompletionSource<Idas3RelayRoute>();
            var requests = new Queue<Task<Idas3RelayRoute>>(); requests.Enqueue(first.Task); requests.Enqueue(second.Task);
            var delayed = new Allocator { Request = () => requests.Dequeue() };
            using (var transport = new Idas3UnityRelayTransport(delayed, true)) {
                int errors = 0; transport.Error += _ => ++errors;
                transport.Host("test"); transport.Host("duplicate");
                Check(transport.IsBusy && delayed.Requests == 1 && transport.RoomCode == "", "one allocation in flight; no premature code");
                transport.Leave(); transport.Host("new room");
                first.SetResult(Route(code: "OLD123")); transport.Poll();
                Check(transport.IsBusy && transport.RoomCode == "" && !transport.CheckEndpoint.IsValid, "stale success cannot replace newer operation");
                second.SetResult(Route(code: "NEW123")); transport.Poll();
                Check(!transport.IsBusy && transport.IsHost && transport.RoomCode == "NEW123", "new room becomes ready only after bind");
                Check(errors == 0, "cancel is not an error");
            }

            double clock = 0;
            var late = new TaskCompletionSource<Idas3RelayRoute>();
            using (var transport = new Idas3UnityRelayTransport(new Allocator { Request = () => late.Task }, true, () => clock)) {
                int errors = 0; transport.Error += _ => ++errors;
                transport.Host("timeout"); clock = 31; transport.Poll(); late.SetResult(Route()); transport.Poll();
                Check(errors == 1 && !transport.IsBusy && transport.RoomCode == "" && !transport.CheckEndpoint.IsValid, "timeout cannot resurrect route");
            }
            var failure = new TaskCompletionSource<Idas3RelayRoute>();
            using (var transport = new Idas3UnityRelayTransport(new Allocator { Request = () => failure.Task }, true)) {
                int errors = 0; transport.Error += _ => ++errors;
                transport.Host("cancelled"); transport.Leave(); failure.SetException(new InvalidOperationException("late failure"));
                Check(errors == 0 && !transport.IsBusy, "stale failure ignored");
            }
            var afterDispose = new TaskCompletionSource<Idas3RelayRoute>();
            var disposed = new Idas3UnityRelayTransport(new Allocator { Request = () => afterDispose.Task }, true);
            disposed.Host("disposed"); disposed.Dispose(); afterDispose.SetResult(Route()); disposed.Poll(); disposed.Dispose();
            Check(!disposed.Available && !disposed.Connected && !disposed.CheckEndpoint.IsValid, "dispose invalidates callbacks and is idempotent");

            using (var host = new Idas3UnityRelayTransport(new Allocator { Request = () => Task.FromResult(Route()) }, true)) {
                host.Host("loopback"); host.Poll();
                ushort port = host.CheckEndpoint.Port;
                Check(port > 0 && host.IsHost && !host.Connected, "host waits on a real ephemeral UDP socket");
                using (var guest = new Idas3UnityRelayTransport(new Allocator { Request = () => Task.FromResult(Route(port)) }, true)) {
                    int hostErrors = 0, guestErrors = 0;
                    host.Error += _ => ++hostErrors; guest.Error += _ => ++guestErrors;
                    guest.Join("abc123");
                    var clients = new[] { host, guest };
                    Pump(clients, () => host.Connected && guest.Connected);
                    Check(host.Connected && guest.Connected && !guest.IsHost && !guest.IsBusy, "two actual UTP clients admitted");

                    var receivedHost = new List<byte[]>(); var receivedGuest = new List<byte[]>();
                    host.Message += receivedHost.Add; guest.Message += receivedGuest.Add;
                    long sentBefore = host.BytesSent;
                    var expectedHost = new List<byte[]>(); var expectedGuest = new List<byte[]>();
                    for (int i = 0; i < 30; ++i) {
                        var a = Payload(i, i % 3 == 0 ? 4096 : 1250);
                        var b = Payload(100 + i, i % 2 == 0 ? 4096 : 1064);
                        expectedGuest.Add((byte[])a.Clone()); expectedHost.Add((byte[])b.Clone());
                        host.Send(a, true); guest.Send(b, true);
                        a[0] = 255; b[0] = 255;
                    }
                    Pump(clients, () => receivedHost.Count == 30 && receivedGuest.Count == 30);
                    for (int i = 0; i < 30; ++i) {
                        Check(Equal(expectedHost[i], receivedHost[i]), "guest reliable fragmentation/order/ownership " + i);
                        Check(Equal(expectedGuest[i], receivedGuest[i]), "host reliable fragmentation/order/ownership " + i);
                    }
                    long expectedBytes = 0; foreach (var packet in expectedGuest) expectedBytes += packet.Length;
                    Check(host.BytesSent - sentBefore == expectedBytes, "payload byte counter after backpressure");
                    Check(hostErrors == 0 && guestErrors == 0, "reliable window backpressure preserved connection");

                    receivedHost.Clear(); receivedGuest.Clear();
                    for (int i = 0; i < 12; ++i) { host.Send(Payload(i, 1064), false); guest.Send(Payload(i + 20, 1064), false); }
                    Pump(clients, () => receivedHost.Count == 12 && receivedGuest.Count == 12);
                    for (int i = 0; i < 12; ++i) {
                        Check(Equal(Payload(i + 20, 1064), receivedHost[i]), "guest unreliable game packet " + i);
                        Check(Equal(Payload(i, 1064), receivedGuest[i]), "host unreliable game packet " + i);
                    }

                    using (var extra = new Idas3UnityRelayTransport(new Allocator { Request = () => Task.FromResult(Route(port)) }, true)) {
                        bool rejected = false; extra.Error += _ => rejected = true;
                        int countBefore = receivedHost.Count;
                        extra.PeerChanged += () => { if (extra.Connected) extra.Send(Payload(200, 32), true); };
                        extra.Join("ABC123"); Pump(new[] { host, guest, extra }, () => rejected);
                        Check(rejected && host.Connected && guest.Connected && receivedHost.Count == countBefore,
                            "third peer rejected before session message delivery");
                    }

                    receivedHost.Clear(); host.Message += _ => host.Leave();
                    guest.Send(Payload(220, 4096), true);
                    Pump(clients, () => !host.Connected);
                    Check(receivedHost.Count == 1 && !host.Connected && host.RoomCode == "", "leave inside receive callback releases driver safely");
                    Pump(clients, () => !guest.Connected);
                    Check(!guest.Connected, "other peer sees orderly disconnect");
                }
            }
            return checks;
        }

        static void Pump(Idas3UnityRelayTransport[] transports, Func<bool> complete)
        {
            var timer = Stopwatch.StartNew();
            while (!complete() && timer.Elapsed.TotalSeconds < 12) {
                foreach (var transport in transports) transport.Poll();
                Thread.Sleep(2);
            }
            if (!complete()) throw new InvalidOperationException("Unity Relay loopback check timed out.");
        }
        static byte[] Payload(int tag, int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; ++i) data[i] = (byte)((i * 13 + tag) % 251);
            return data;
        }
        static bool Equal(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; ++i) if (left[i] != right[i]) return false;
            return true;
        }
    }
}
#endif
