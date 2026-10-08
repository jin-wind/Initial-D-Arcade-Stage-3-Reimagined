# EOS setup and validation

EOS is the selected Internet transport provider as of 2026-10-08. The current
game still contains the Unity Relay adapter; an EOS game transport has not yet
been integrated. LAN remains available. See
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
The `EOS service probe` workflow uses them only in its runtime test step.
The game itself does not yet consume the EOS settings.

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
4. Game integration: implement `IIdas3Transport` using EOS, retaining the
   game's compatibility handshake and rollback simulation. Validate payloads
   up to 4096 bytes with bounded fragmentation; EOS packets are limited to
   1170 bytes. This check is pending.
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

Before a Unity 6000.6 build, check the Android dependency library's Gradle
configuration, iOS plugin filtering in `Idas3IOSBuild.ConfigurePlugins`, and
IL2CPP stripping. Existing Unity-only package exemptions currently exclude
third-party EOS native plugins from iOS exports. No Unity 6000.6 EOS player has
been compiled in this project yet.

## Official references

- [Clients and policies](https://dev.epicgames.com/docs/epic-online-services/eos-fundamentals/client-and-client-policy/client-policy-guide)
- [Device ID and Connect](https://dev.epicgames.com/docs/epic-online-services/eos-fundamentals/connect-interface/connect-reference#device-ids)
- [P2P and Relay](https://dev.epicgames.com/docs/epic-online-services/multiplayer/nat-p2p-interface/p2p-reference)
- [Android](https://dev.epicgames.com/docs/epic-online-services/platforms/android)
- [iOS](https://dev.epicgames.com/docs/epic-online-services/platforms/i-os)
