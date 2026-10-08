# Unity Relay preparation validation

Date: 2026-10-08. Editor: Unity 6000.6.4f1 on Windows x64 and macOS ARM64.

The first Relay transport, mobile Internet/LAN selection and CI configuration
staging are implemented. No Unity Cloud Project has been supplied or enabled
for this game yet. These checks do **not** establish live Relay connectivity.

## Results

| Check | Android target / Windows Editor | iOS target / Mac Editor |
| --- | --- | --- |
| Existing touch controls and LAN checks | 2,899 passed | 2,899 passed |
| New Relay adapter checks | 108 passed | 108 passed |
| Actual player C# compilation (without `UNITY_EDITOR`) | 35 assemblies, passed | 34 assemblies, passed |
| Unity process exit | 0 | 0 |

The adapter checks use real UTP UDP sockets on loopback and a substituted
allocation provider. They cover room-code input, an unconfigured project,
duplicate requests, cancellation, stale success/failure, timeout, disposal,
two-way reliable 4,096-byte fragmentation, order and caller buffer ownership,
reliable-window backpressure, unreliable 1,064-byte gameplay payloads, payload
counters, rejection of a third peer, leaving inside a receive callback, and
peer disconnection. No real Unity allocation or player profile is used.

The configuration script passed seven isolated checks: missing ID does not
modify the project, malformed ID does not write partial configuration, a valid
UUID is staged, environment is retained, and Ads/Analytics remain disabled.
Both workflow YAML files parse, and `git diff --check` passes.

## Reproduce

Run Unity in batch mode with the appropriate project/target and one of:

- `-executeMethod Idas3UnityRelayTests.Run`: adapter/socket checks only.
- `-executeMethod Idas3UnityRelayTests.CheckAndroidPlayer -buildTarget Android`:
  existing controls/LAN checks, adapter checks and Android player scripts.
- `-executeMethod Idas3UnityRelayTests.CheckIOSPlayer -buildTarget iOS` on a Mac:
  the same checks, iOS plugin configuration and iOS player scripts.

Reports are written under `Logs/touch-controls-tests.json`,
`Logs/unity-relay-tests.json`, and `Logs/unity-relay-player-<target>.json`.
The normal Android/iOS CI build methods also call the adapter checks.

## Integration issues resolved

The bundled Discord Newtonsoft DLL was overriding Unity's JSON package and
lacked `AotHelper`, preventing Services SDK compilation. Its importer is now
disabled; Discord and UGS use `com.unity.nuget.newtonsoft-json` 3.2.2. No plugin
binary was changed or added.

Unity 6000.6.4f1 resolves Transport, Collections and Burst to built-in versions
6.6.0, 6.6.0 and 2.0.0 respectively. The manifest names the resolved Transport
version explicitly; `packages-lock.json` records the tested dependency graph.
The iOS native importer setup preserves `Packages/com.unity.*` metadata while
continuing to exclude desktop Steam/native game plugins from iOS.

## Not yet validated

- A fresh APK or IPA with these changes, IL2CPP conversion/linking, installation
  and on-device Relay SDK initialization.
- Anonymous Authentication, allocation, DTLS bind, or room-code joins against
  an actual Unity Cloud Project.
- Android/iOS races over real Relay, complete finish/rematch, mobile
  backgrounding, two different carriers, latency/jitter or billable bandwidth.

The earlier LAN cross-play results remain documented separately in
[mobile-crossplay-validation.md](mobile-crossplay-validation.md). They cannot
be substituted for these pending internet checks. Continue using
[unity-relay-setup.md](unity-relay-setup.md) after creating the Cloud Project.
