# EOS service validation

Date: 2026-10-08. Native SDK: EOS 1.19.2.1, shipped in Epic Online Services
Plugin for Unity 6.2.0. Test tool: `Tools/EOSProbe`, .NET 10 Windows x64 console.
This first section records the original desktop probe. The Android game
integration and its current runtime blocker are recorded below.

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

## Android game APK, 2026-10-09

The actual game now has an Android EOS transport behind the existing INTERNET
menu. [Actions run 37934929850](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37934929850)
at commit `5615f3ab35e40960a18e88794fa91403c4b8f150` built the full ARM64 APK:
2,329,383,553 bytes, including EOS native library with 16 KB ELF alignment.
The existing APK content/signature checks passed. Both Android 15 emulators
installed it and reached the game after reusing and verifying the existing
17,965 runtime data files. The Internet menu displayed EOS RELAY.

Pressing HOST A BATTLE exposed an Android JNI registration issue:
`UnsatisfiedLinkError` for `EOSLogger.Log`. The SDK Java initialization ran
before `System.loadLibrary("EOSSDK")` had registered its native methods.
Commit `1cdf0fba20e721ab2486df8a88203907bb94c7cf` loads the library through the
game Activity and checks availability before SDK initialization.

A local diagnostic APK recompiles/replaces only that Activity in the successful
CI game's DEX, retaining other classes, native libraries and resources, and
uses the existing local signing key. It installed and started successfully.
HOST A BATTLE no longer crashed; it returned `AuthWrongClient` during Device ID
creation. The official OAuth endpoint also returned HTTP 401 `invalid_client`
for the same configuration read in memory from the APK. The client, product,
sandbox and deployment IDs match current Actions variables. The secret/token
were not printed or written to diagnostic files.

At that point the next required step was to update the matching EOS client
credentials and rebuild. Room creation, joining and race traffic had not yet
been verified in the game.
The earlier Windows probe success predates the currently failing configuration
and does not override this Android runtime result. The local diagnostic APK is
not the output of the subsequent full CI rebuild.

The owner updated `EOS_CLIENT_SECRET` at 2026-10-09 14:50:51 UTC. The next
[build 37947404601](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37947404601)
read that update but still received HTTP 401 from Epic before compiling the
APK. That attempt was blocked by the matching client ID/secret pair, not an APK
compilation error. Build 37945122078, which had staged the previous credentials,
was cancelled to avoid producing another artifact with rejected credentials.

## Android game rooms and race-start failure, 2026-10-10

The subsequent credential correction was accepted in
[run 37949703521](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37949703521),
which built commit `02a05405a9fed1a7950ec9f27c86ef41df0266d1`. This complete CI
APK was installed on both Android 15 emulators. Actual game logs confirmed
Connect sign-in, room creation/joining and `RelayedConnection` on both sides.
The game's compatibility handshake completed and both drivers could ready up.
The credential error is therefore historical, not the current blocker.

Starting the race failed: the host logged `Start acknowledgement was not
received. Please reconnect and retry.`, and the guest logged `Connection to
the other driver was lost.` Native race loading had already succeeded. The
neutral finish and later offline selection menus do not establish a successful
online race.

Commit `56cf108a94b5e935d1ce4846fd8e54353c16218a` extends the scheduled start
window and retries the start message while waiting for its acknowledgement.
[Run 38064831810](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/38064831810)
built and verified its complete APK. On 2026-10-15 the manually downloaded
artifact's SHA-256 matched the GitHub artifact digest:

```text
f7435a0954a64c61974d9f3c8025523625bf1a51ec8e142f6e4a01c3f23d07a2
```

The APK is 2,329,382,509 bytes. Its actual gameplay results are recorded below.

## Android game driving, 2026-10-15

Both Android 15 emulators were updated in place to the exact `56cf108a` CI APK,
retaining their ROM, saves and extracted resources. Each game's startup verified
all 17,965 runtime files (3,943,133,420 bytes). Initial concurrent streamed ADB
installs failed while the emulator system services restarted. Sequential
non-streaming installs succeeded. The emulator disk images were then copied
from the shared mechanical disk to an SSD to resolve long filesystem waits;
the original images were retained. These environment changes mean this run
does not isolate the start-window patch from storage-performance effects.

The installed ARM64 release game ran through Android's ARM translation layer
on two x86-64 emulator images. Tests used the normal touchscreen ONLINE menu,
INTERNET / EOS RELAY, room-code entry, Ready and Start Battle. No game binary,
simulation checks or network protocol were replaced for these runtime tests.

| Test | Result |
| --- | --- |
| Emulator A hosts; B joins, start at 07:35 UTC | Both signed in, completed the game handshake, reported `RelayedConnection`, started and drove |
| Emulator B hosts; A joins, start at 07:43 UTC | Both joined, reported `RelayedConnection`, started and drove after swapping roles |
| B hosts; A joins, start at 07:51 UTC | Let the race time out, selected Continue on both clients, and returned to the same room with both drivers and Ready reset |
| Rematch in that room, start at 07:57 UTC | Both readied again, started and drove without recreating the room or restarting either game process |

The first two races used AE86 cars on Akina downhill, dry/night, with boost and
collisions enabled. Touchscreen throttle input moved both cars; screenshots showed the
opponent in the main view or mirror and changing relative distances. Each of
these races ran to the initial 70-second race time limit. The second run's
captures also show the original Continue screen. Both rooms were subsequently cleaned
up successfully (`destroy room: Success` / `leave room: Success`).

The third race exercised the time-limit result and Continue flow. Touch input
advanced the result screens and selected Yes on both devices. Captures at
07:53 UTC show both drivers back in the same EOS room with Ready reset. After
readying again, both entered the rematch and accepted throttle input. Captures
about 58 seconds into the rematch show both cars past the starting straight,
with the opponent visible and matching opposite-signed relative distances.
Neither game process restarted between that room's two races.
After the rematch's Continue timeout, the host logged `destroy room: Success`;
270 ms later the guest logged `leave room: NotFound`. Both returned to course
selection. The callback ordering is consistent with the room already being
destroyed when the guest left; no race error was reported.

None of these runs reported the previous start-acknowledgement timeout, a race
disconnect or a desynchronization error. This is observation of the game's
existing runtime checks, not an independently recorded per-frame hash trace.
Neither car completed the entire course. Full course finishes, mobile-carrier
connections, physical-device performance and iOS EOS crossplay remain untested.
The emulators share one host/network, even though their traffic used EOS relays.
