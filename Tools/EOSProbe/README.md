# EOS Service Probe

Windows x64 .NET 10 console using the already downloaded official EOS 1.19.2.1
managed bindings and native DLL. It does not use the Unity project.

Build:

```powershell
dotnet build EosProbe.csproj --configuration Release '-p:EosPackage=C:\path\to\unpacked\package'
```

The default SDK directory is `../idas3-eos-sdk/package`. CI can override it with
`-p:EosPackage=<absolute-path-to-unpacked-package>`.

Execute `bin/Release/net10.0/EosProbe.exe` with these process environment variables
already set: `EOS_PRODUCT_ID`, `EOS_SANDBOX_ID`, `EOS_DEPLOYMENT_ID`,
`EOS_CLIENT_ID`, and `EOS_CLIENT_SECRET`. No command-line credentials are accepted.
Missing configuration causes an immediate exit before loading the native SDK.

In single mode, the probe initializes EOS, reuses or creates a Device ID without deleting it,
authenticates via Connect, creates a two-member public lobby, adds a unique test
attribute, searches and reads that lobby, and destroys it in cleanup. It does not
join other lobbies or enumerate unrelated player data. Calls are time-bounded;
Ctrl+C requests cancellation and cleanup.

Only structured phase/result diagnostics are printed. Raw SDK logs, credentials,
tokens, full account IDs, and arbitrary exception messages are not printed.
The SDK cache is scoped to `probe-cache` under the executable directory. EOS may
persist its Device ID using its normal platform mechanism; the probe never resets
or deletes that identity.

`EOS_PROBE_ROLE` selects `single` (default), `host`, or `join`. The single role
performs the original login and lobby write/search/read test. Exit code 0 in this
mode does not prove relay connectivity or mobile runtime support.

For the two-peer test, launch host and join on separate machines with the same
credentials and the same unique `EOS_PROBE_RUN_ID`. The run ID is a nonsecret
marker of 1-64 ASCII letters, digits, hyphens, or underscores, such as a GitHub
workflow run ID plus attempt number. Separate machines are needed because EOS
Device ID login on the same Windows user can resolve to the same player.

`EOS_PROBE_WAIT_SECONDS` controls lobby discovery and the wait for a second
member. It defaults to 240 seconds and accepts 30-600. Each ordinary SDK callback
still has a maximum 30-second wait, P2P exchange has a 75-second deadline, and
cleanup is bounded. A workflow-level timeout should allow setup/download time
in addition to these waits.

Host creates a public two-person lobby carrying the marker. Join searches for
the exact marker, validates its bucket and capacity, then joins. Each side reads
the lobby member list and only accepts P2P from that single authenticated peer
on the probe socket. Both require the EOS connection callback to report
`RelayedConnection` after setting `ForceRelays`.

The test exchanges deterministic 100-byte reliable and unreliable messages in
both directions with content verification and reliable ACKs. It then exchanges
completion messages/ACKs; both keep pumping EOS before closing, with the host
draining longer to let the guest leave first. It removes notification handles,
closes the P2P connection, and destroys/leaves the lobby in cleanup.

JSON lines always include `phase`, `result`, `role`, `elapsedMs`, and `detail`.
An expected peer run includes:

```json
{"phase":"peerConnectionEstablished","result":"RelayedConnection","role":"host"}
{"phase":"receiveReliable","result":"Success","role":"host"}
{"phase":"receiveUnreliable","result":"Success","role":"host"}
{"phase":"ackReliable","result":"Success","role":"host"}
{"phase":"ackUnreliable","result":"Success","role":"host"}
{"phase":"completionHandshake","result":"Success","role":"host"}
{"phase":"summary","result":"Passed","role":"host","detail":{"hasPuid":true,"cleanupOk":true,"relayedConnection":true,"reliableReceived":true,"unreliableReceived":true,"reliableAcked":true,"unreliableAcked":true,"completionHandshake":true}}
```

The examples omit timing fields for brevity. Require `summary: Passed` in **both**
roles. Exit code 0 in peer mode proves the desktop test used Epic relay servers
and exchanged data; it does not prove Android/iOS binary integration, actual
carrier reachability, or a playable racing session.
