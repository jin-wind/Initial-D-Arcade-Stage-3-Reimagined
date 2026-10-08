# EOS service validation

Date: 2026-10-08. Native SDK: EOS 1.19.2.1, shipped in Epic Online Services
Plugin for Unity 6.2.0. Test tool: `Tools/EOSProbe`, .NET 10 Windows x64 console.
The game does not yet contain an EOS transport or an EOS-enabled mobile build.

## Credentials, login and lobby permissions

The supplied product's client-credentials REST request returned HTTP 200.
The Windows SDK then created/reused a Device ID and signed in through Connect.
The initial Custom client policy returned `AccessDenied` when creating a lobby.
After the owner selected Peer2Peer, the same SDK probe created a two-member
lobby, wrote a unique attribute, searched and read the lobby, then destroyed it.
The local run passed in approximately 13 seconds, including cleanup.

Public product identifiers are repository variables and the client secret is
an Actions secret. No credentials, tokens or full player IDs are included in
the probe's reports or committed source.

## Real relay traffic

Two independent hosted Windows runners used the same product and deployment.
The host advertised an isolated test marker; the guest searched for that marker
and joined. Both checked that the lobby contained their own player and exactly
one different player before accepting traffic on the test socket.

Both successful runs used source commit `c4e199d6615639d45944298c8d0fe20c88adc4d0`:

| Attempt | Host | Guest | Evidence |
| --- | --- | --- | --- |
| [37770627046 / 1](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37770627046/attempts/1) | Passed, 19.5 s | Passed, 23.1 s | Both native callbacks reported `RelayedConnection`; both payload types and cleanup passed |
| [37770627046 / 2](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37770627046/attempts/2) | Passed, 28.1 s | Passed, 9.9 s | Fresh runners and room marker; all the same checks passed |

Times are complete probe durations, including sign-in, peer arrival and cleanup;
they are not ping or packet-latency measurements.

Passing requires `SetRelayControl(ForceRelays)` to succeed and the native
connection callback to report `RelayedConnection`. Each side sends and checks
deterministic 100-byte reliable and unreliable messages on separate channels,
receives acknowledgements, performs a completion handshake and cleans up.
The guest leaves first; the host destroys the room. The final summaries require
all of these flags to be true:

```json
{
  "hasPuid": true,
  "cleanupOk": true,
  "relayedConnection": true,
  "reliableReceived": true,
  "unreliableReceived": true,
  "reliableAcked": true,
  "unreliableAcked": true,
  "completionHandshake": true
}
```

Attempt 2 reports: [host](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37770627046/artifacts/11548065971),
[guest](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37770627046/artifacts/11548090847).
Diagnostic artifacts are retained for seven days. SDK warnings are reduced to
category, error identifiers, HTTP status codes and allowlisted diagnostic terms;
their raw messages are not emitted.

## Observed failure and limits

The first [run 37769953661](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37769953661)
successfully logged in and joined the lobby but the guest's P2P setup returned
`ConnectionFailed`; the host then received `ClosedByPeer` during guest cleanup.
Neither side received any gameplay payload. Cleanup passed on both sides.
Adding diagnostic logging was the only subsequent behavioral change to the
probe; the root cause of the first failure has not been established. Two later
successes establish availability, not arbitrary-network reliability or a fix
for that initial failure. Production connection failure and retry behavior
still needs implementation and testing.

Not validated by these checks:

- Unity 6000.6 EOS native-plugin integration or IL2CPP mobile players.
- Android/iPhone game sessions, actual mobile carriers, backgrounding or locks.
- Game packets larger than 100 bytes, 4096-byte fragmentation, race sync,
  full finish/rematch, third-player rejection or long-running token renewal.
- Latency, bandwidth, packet loss and service behavior under load.

EOS's supplied ARM64 framework is for iPhoneOS. Epic does not support EOS SDK
testing on iOS Simulator; cross-platform device verification requires a signed
build on a physical iPhone. See [eos-setup.md](eos-setup.md) for platform and
credential configuration.
