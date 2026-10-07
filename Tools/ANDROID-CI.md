# Android CI and the shared mobile build

The `Android APK` workflow builds an ARM64/Vulkan APK on a GitHub-hosted Linux
runner. It runs on pushes to `android-ci`, on `android-v*` tags, and by manual
dispatch where GitHub exposes the workflow. The game data is included; players
import their own ROM on first launch.

Set `UNITY_LICENSE`, `UNITY_EMAIL` and `UNITY_PASSWORD` as repository Actions
secrets. The workflow checks their presence without printing them. The account
credentials must correspond to the Unity license; GitHub Pro is a separate
subscription and does not provide a Unity or Apple development license.

The build compiles the native plugin using NDK r27c, stages and hashes the
runtime data, then calls `Idas3TouchControlsTests.BuildAndroid` in Unity
6000.6.4f1. The artifact is uploaded only after runtime SHA-256 checks, ARM64
ELF alignment checks, APK signature verification and ZIP alignment verification
pass. A failed build uploads available diagnostics for seven days.

Download a successful run's `InitialDUnity-android-<commit>` artifact on the
Actions page. It contains the APK and verification reports and expires after
30 days. The large APK is not attached to a GitHub Release. With no release
keystore configured, the hosted build uses a debug key; do not assume it can
update a locally signed installation. Consistent signing is a separate step.

## Android and iOS from the same commit

The iOS work already exists in a separate Mac project and is being integrated
on `mobile-ci`. Reuse its native archive builder, Metal rendering support,
`__Internal` bindings, mobile menus and numeric/full-version mapping. Do not
start a second iOS port or replace these with Android-only copies.

The existing iOS exporter requires a macOS Editor. A complete iOS job needs
Xcode, Unity's iOS module, a matching `iphoneos` native archive, a staged data
manifest, a Unity export and an Xcode device build. An ARM64 Simulator archive
is a different SDK binary and must never be substituted for the device archive.
Simulator `.app` artifacts are useful for testing without an iPhone; a
device-installable IPA additionally needs an appropriate signing arrangement.

Both build jobs must use the same source commit, full release label, protocol
identity and simulation data. iOS's numeric bundle version alone must not
participate in the cross-platform compatibility comparison.

## Interoperability acceptance

First validate direct LAN connection on the same Wi-Fi: Android hosts/iOS joins
and iOS hosts/Android joins, lobby state, ready/start, a race and rematch, then
disconnect and background handling. A simulator can establish an initial
baseline; real iOS hardware is still needed for local-network permissions,
multi-touch, sensor steering and device graphics/performance.

The current Android room list uses UDP broadcast. iOS local-network permission
alone does not authorize broadcast/multicast on physical devices. Preserve
manual address entry while adapting room discovery (for example, Bonjour)
or arranging Apple's multicast entitlement. Treat internet matchmaking and
relay/NAT traversal as a later milestone; the current LAN work does not provide
them. A shared ARM64 CPU architecture also does not prove deterministic
simulation across Android and iOS toolchains.

References:

- https://game.ci/docs/github/activation
- https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy
- https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.developer.networking.multicast
