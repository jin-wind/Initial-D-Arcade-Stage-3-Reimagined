# Unity Relay preparation validation

Date: 2026-10-08. Editor: Unity 6000.6.4f1 on Windows x64 and macOS ARM64.

The Relay transport, mobile Internet/LAN selection and CI configuration are
implemented. A user-supplied Unity Cloud Project is now configured, but the live
Relay service rejects this project's access with HTTP 451. The packages build
successfully; Internet rooms are blocked by service availability.

## Configured build and live service check

GitHub Actions run [37761114638](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37761114638)
successfully built commit `ac376f7fc4e1cb56e02f04f44be5b15c08c87f62` for all
three targets on 2026-10-08. Repository variables supplied the project's ID
and `production` environment. All three configuration logs contain
`Unity Relay build configuration staged.` No project credentials were committed.

| Package | Configured build artifact |
| --- | --- |
| Android ARM64 APK | [Download artifact](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37761114638/artifacts/11544511089) |
| ARM64 iOS Simulator app ZIP | [Download artifact](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37761114638/artifacts/11544101581) |
| ARM64 unsigned iPhone IPA | [Download artifact](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37761114638/artifacts/11543631716) |

Packages expire on 2026-11-07; diagnostics expire on 2026-10-15. The Android
verification step passed resource hashing, APK signature and 16 KB alignment
checks. Both Apple package verification steps passed. Device signing is still
required for the unsigned iPhone IPA.

A workstation REST probe using the SDK's endpoints and the supplied project
returned HTTP 200 for anonymous Authentication in `production`. A Relay
`GET /v1/regions` using the resulting player token returned:

```json
{"status":451,"code":53,"title":"Unavailable For Legal Reasons","detail":"Unavailable in your region"}
```

No allocation was created, and no player token was logged or saved. This is a
live service availability check, not an on-device SDK or gameplay result.
[Unity's official notice](https://support.unity.com/hc/en-us/articles/48560161446804-Unity-Services-Access-Ending-June-30-2026-Information-for-China-Based-Developers)
states that organizations registered in Mainland China, Hong Kong or Macau
lose global Relay access from 2026-06-30, while Authentication remains available.
The project's actual organization address has not been inspected. Rebuilding
the same project cannot resolve this service restriction.

## Earlier unconfigured build

GitHub Actions run [37740339166](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37740339166)
completed successfully for commit `28b74dd2527407ab96cc93a75d9fffbf8b252bab`
on 2026-10-08. Android APK, iOS Simulator app, and unsigned iPhone IPA were
built, verified, and uploaded as workflow artifacts. The package artifacts
expire on 2026-11-07; diagnostic log artifacts expire on 2026-10-15.

| Package | Artifact |
| --- | --- |
| Android ARM64 APK, with game data and without ROM | [Download artifact](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37740339166/artifacts/11534704686) |
| iOS Simulator ARM64 app ZIP | [Download artifact](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37740339166/artifacts/11535158029) |
| iPhone ARM64 unsigned IPA | [Download artifact](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37740339166/artifacts/11534342311) |

The build used no `UNITY_PROJECT_ID`, so the packages contain the Relay code
but are not configured for live Internet rooms. The iPhone IPA is unsigned;
it cannot be installed on a device until an Apple signing and provisioning
profile workflow is configured. The Simulator ZIP has an ad-hoc signature.

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

- Installation and startup of these exact CI artifacts on physical devices.
- On-device Relay SDK initialization.
- Anonymous Authentication from a built player, allocation, DTLS bind, or
  room-code joins. The workstation probe above establishes only REST sign-in
  and the current Relay access failure.
- Android/iOS races over real Relay, complete finish/rematch, mobile
  backgrounding, two different carriers, latency/jitter or billable bandwidth.

The earlier LAN cross-play results remain documented separately in
[mobile-crossplay-validation.md](mobile-crossplay-validation.md). They cannot
be substituted for these pending internet checks. Check service eligibility in
[unity-relay-setup.md](unity-relay-setup.md) before further Internet tests.
