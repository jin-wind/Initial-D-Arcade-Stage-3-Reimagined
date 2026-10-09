# EOS setup and validation

EOS is the selected Internet transport provider as of 2026-10-08. Android
builds staged with `Tools/prepare-eos-android.py` now use the EOS adapter for
the game's INTERNET option. LAN remains available. Unstaged builds and the
current iOS builds retain the previous Unity Relay adapter. See
[the Unity validation record](unity-relay-validation.md) for the HTTP 451 that
blocked the previous provider.

## Product configuration

Use one EOS Product, Sandbox and Deployment for both mobile platforms. Under
Developer Portal > Product Settings, collect the following values:

| GitHub Actions setting | Type | Portal field |
| --- | --- | --- |
| `EOS_PRODUCT_ID` | Repository variable | Product ID |
| `EOS_SANDBOX_ID` | Repository variable | Sandbox ID |
| `EOS_DEPLOYMENT_ID` | Repository variable | Deployment ID |
| `EOS_CLIENT_ID` | Repository variable | Client ID |
| `EOS_CLIENT_SECRET` | Repository secret | Client Secret |

The fork's values have been saved. They are not committed to the repository.
The Android APK workflow stages the official SDK and generates the ignored
`Assets/Resources/Idas3EOS.json` from these settings before building. No secret
is committed to source or printed by the staging script. This is a game-client
configuration shipped in the APK; the Peer2Peer policy remains essential.

The intended sign-in path is Connect Device ID, followed by Connect Login and
CreateUser when necessary. This path does not require an Epic Account Services
Application ID or a player Epic account. A sample EncryptionKey consisting of
placeholder digits is not used for Connect, Lobbies or P2P.

Use a policy suitable for an untrusted game client. Epic's Peer2Peer preset is
the starting point; verify an existing Custom policy through actual SDK calls.
Do not give a public game administrative or trusted-server permissions merely
to pass a test. A client secret supplied to a distributed game is extractable;
its permissions must be appropriate for a game client even when the build
input is stored in GitHub Secrets.

## Verification stages

1. Client authentication: the configured deployment returned HTTP 200 from
   `POST https://api.epicgames.dev/auth/v1/oauth/token` using the client
   credentials grant. No access token was printed or saved. This confirms the
   credentials are accepted, not that Device ID, Lobbies or P2P are authorized.
2. SDK authentication and lobby access: passed on Windows using the official
   native SDK. Device ID creation and Connect sign-in succeeded. The initial
   Custom client policy returned `AccessDenied` for CreateLobby. After the
   product owner changed it to Peer2Peer, the probe created a two-player lobby,
   updated a unique test attribute, searched and read it back, and successfully
   destroyed it. The successful run took about 13 seconds. No room was left
   open and no credentials were included in the report.
3. Relay traffic: passed in two consecutive attempts on separate Windows
   GitHub runners. Both distinct Product User IDs joined the lobby, set
   `RelayControl.ForceRelays`, received `RelayedConnection` notifications and
   exchanged verified reliable and unreliable 100-byte payloads in both
   directions. Details and the earlier failed attempt are recorded in
   [eos-validation.md](eos-validation.md). This is not a mobile gameplay test.
4. Game integration: the Android adapter implements `IIdas3Transport`, retaining the
   game's compatibility handshake and rollback simulation. Validate payloads
   up to 4096 bytes with bounded fragmentation; EOS packets are limited to
   1170 bytes. Runtime race validation is pending.
5. Mobile validation: Android and a physical iPhone must complete races and
   rematches using different networks. This check is pending.

## SDK and build constraints

The inspected package is Epic Online Services Plugin for Unity 6.2.0, from
[the official release](https://github.com/EOS-Contrib/eos_plugin_for_unity/releases/tag/v6.2.0).
Its SDK headers identify EOS 1.19.2.1, despite an older version string in the
package description. The Windows native DLL has a valid Epic Games signature.
Android libraries are staged from `PlatformSpecificAssets~`; their ARM64 and
x86_64 ELF load segments have 16 KB alignment.

The package supplies an ARM64 iPhoneOS framework with a minimum iOS version of
15.0. It does not contain an iOS Simulator slice. Epic's iOS documentation also
states that EOS SDK testing is not supported in Xcode Simulator. Keep Simulator
single-player/LAN builds separate from evidence of EOS functionality. Physical
iPhone tests require device signing and provisioning.

The Android build stages Unity 6-compatible Gradle source sets and enables
Java library desugaring. The first full EOS game APK built successfully in
[run 37934929850](https://github.com/jin-wind/Initial-D-Arcade-Stage-3-Reimagined/actions/runs/37934929850)
and was installed on two Android 15 emulators. Both reached the game and the
INTERNET menu displayed EOS RELAY. Runtime testing found a missing JNI library
load before Java initialization; the fix explicitly loads EOSSDK through the
application's class loader.

The locally rebuilt Activity fixed that crash. The next on-device result was
`EOS device identity: AuthWrongClient`. A direct request to Epic's official
OAuth endpoint with the *same configuration embedded in the APK* returned
HTTP 401 `errors.com.epicgames.common.oauth.invalid_client`. All four public
product identifiers match the repository variables. Update the matching
`EOS_CLIENT_ID` and `EOS_CLIENT_SECRET` before rebuilding. No room has yet
been created by this game APK, and Internet racing has not been demonstrated.
iOS EOS native-plugin integration is still pending; existing iOS artifacts
are not evidence of EOS connectivity.

## Playing with the Android EOS APK

Both players install the same EOS-enabled game APK. Open ONLINE, select
INTERNET, then choose HOST A BATTLE. Share the eight-character room code.
The other player uses JOIN WITH CODE. Select cars and courses, ready up,
and start the race through the existing game lobby.

Room codes use capital letters and digits 2-9, excluding I and O. EOS performs
Device ID sign-in automatically; players do not need Epic accounts. The
transport forces relays and accepts game packets only from the other
authenticated lobby member. This remains a test build until actual game
sessions have been exercised; it is not yet a validated Android/iOS release.

## Official references

- [Clients and policies](https://dev.epicgames.com/docs/epic-online-services/eos-fundamentals/client-and-client-policy/client-policy-guide)
- [Device ID and Connect](https://dev.epicgames.com/docs/epic-online-services/eos-fundamentals/connect-interface/connect-reference#device-ids)
- [P2P and Relay](https://dev.epicgames.com/docs/epic-online-services/multiplayer/nat-p2p-interface/p2p-reference)
- [Android](https://dev.epicgames.com/docs/epic-online-services/platforms/android)
- [iOS](https://dev.epicgames.com/docs/epic-online-services/platforms/i-os)
