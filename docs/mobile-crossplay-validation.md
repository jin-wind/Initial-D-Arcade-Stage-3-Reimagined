# Shared Android/iOS validation

GitHub Actions run `37705688690` successfully built the shared `ed853d5e`
source as an Android ARM64 APK, ARM64 iOS Simulator app, and unsigned iPhone
IPA. Runtime manifests, binary SDK/architecture checks, APK signature and
alignment checks passed. All 17,965 runtime files were verified.

On 2026-10-08, an Android 15 emulator on Windows joined an iPhone 17 Simulator
host on the remote Mac using the normal direct LAN address. Both games showed
two drivers, exchanged car/course/ready states, and loaded the selected race.
The initial race then rejected a state checksum mismatch.

The fixed-input native probe identified the first difference before simulation
frame zero: the original statistics output computes `0/0` for the accelerator
and brake fractions while no samples exist. Android produced `0xffc00000`,
Apple produced `0x7fc00000`. Physical states and the other sampled fields
agreed. `canonicalRollbackStatistics` normalizes NaNs only in these two float
output lanes in the checksum copy. Original state, integer lanes, signed zero,
finite values, infinities, and all physics/history fields retain their bits.
The compatibility identity is bumped to v2, so old and fixed builds cannot
silently join each other.

The fixed native probe compared 6,001 states each for condition 0 (dry),
1 (reverse), 2 (wet), and 16 (snow), with two different cars, tuning, boost,
acceleration and steering. All simulation and rollback checksums agreed
between Android and iOS Simulator: 24,004 states, zero mismatches.
This is an offline fixture result; it is not a real-device performance result.

## Live simulator game checks after the fix

The normal direct LAN menu was first verified with iOS hosting and Android
joining the Mac's private IP. The deterministic fix was then validated in
the existing opt-in two-player game diagnostic, in both host roles:

| Host | iOS checks | Android checks | Observed source ticks | Car travel |
| --- | ---: | ---: | ---: | --- |
| iOS | 10,456 | 10,515 | 731–746 | 343–380 m |
| Android | 10,515 | 10,441 | 911–939 | 373–485 m |

Both players reported `passed: true`, no error, synchronized authority
digests, lobby readiness, native race load/release, moving local and remote
cars, rendered opponent/HUD views, and orderly peer leave. The diagnostics
ran through ADB/SSH TCP forwards because emulator guests use isolated virtual
networks. They verify actual Android and iOS game processes and the shared
protocol; they do not prove broadcast discovery, internet matchmaking,
physical iPhone LAN permissions, or a complete finish/rematch.

The local Android player used an updated native plugin inside its emulator
installation; the iOS simulator app was rebuilt with the fixed native archive.
Actions run `37715322203` separately built and verified the fixed source
`08fe846b` for all three targets. The packaged fix uses compatibility v2;
the diagnostic hook described below is a subsequent test-tool change.

## Opt-in iOS Simulator integration diagnostic

iOS IL2CPP does not expose `simctl launch` arguments through managed
`Environment.GetCommandLineArgs` in the tested player. In a Debug iOS player,
place `idas3-multiplayer-test-arguments.json` under its `Documents` directory
before launching. Example (use a NEW isolated output directory):

```json
{"args":["-idas3-multiplayer-smoke","/absolute/app/Documents/CrossTests/new-run","-idas3-multiplayer-role","host","-idas3-multiplayer-port","27035","-idas3-multiplayer-world-only"]}
```

For a guest, use role `join` and add `-idas3-multiplayer-address` followed by
the forwarded loopback address. The file is consumed once; release device
players ignore it. The existing diagnostic writes isolated profiles and a
`report.json`, without using ordinary player saves. Wait for the host's
`room.json` before starting the other player. Remove ADB reverse mappings
before making Android the host, so its listener can bind the game port.

`Native/tools/check_mobile_race_digest.cpp <root> [condition] [frames]` emits
the fixture's checksum rows and initial state words. Link against the same
native libraries as the player. Compare `simulation` and `rollback` CSV rows;
raw statistics NaN words deliberately remain platform-specific. The checksum
regression test covers those two lanes, preservation of original state,
integer fields, finite values, signed zero, infinity and physics history.

Windows emulator test environment: Android 15 Google APIs x86_64 image with
ARM64 translation, NVIDIA host Vulkan, and QEMU CPU `android64,+aes`. The AES
feature is necessary for this image's ARM64 translator. The Android 11 image
failed on Unity's SEVL instruction. These emulator limitations are not evidence
of a phone-runtime failure or of physical-device performance.
