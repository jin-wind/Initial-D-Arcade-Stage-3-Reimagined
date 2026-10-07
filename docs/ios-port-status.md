# iOS port — Mac work record (2026-10-07)

## Canonical workspace

`/Users/michael/Projects/InitialD-iOS` on the Apple M4 Mac. Source imported from the current `F:\\UnityProjects\\InitialD-Android-6000.6.4f1` copy, not a fresh GitHub checkout. Original Windows/Android projects were not edited. Unity version remains 6000.6.4f1 (12bfff696524).

## Completed and verified on this Mac

- Separate Git repository (`ios-port`) with a source baseline; source archive SHA-256 verified before extraction.
- Native ARM64 `iphonesimulator` static archive built and force-load link checked.
- Native ARM64 `iphoneos` static archive built and force-load link checked. Device archive is kept outside Assets to avoid duplicate definitions / wrong-SDK linking.
- Portable macOS ARM64 Editor dylib built; actual `ctypes.CDLL` load and `Idas3UnityVersion()==1` check passed.
- An actual iPhone 17 Simulator booted and ran the native ABI probe: `IDAS3_NATIVE_SIMULATOR_SMOKE abi=1`, exit 0. Simulator was shut down afterward because it had been stopped before this test.
- Minimum iOS deployment target is **16.3**: Apple libc++ floating-point `std::to_chars` used in native telemetry is unavailable below that. No replacement serializer was introduced.
- User-approved cleanup of uv, Homebrew downloads, npm package cache, and pip cache only: 9,505,783,808 bytes of additional free space observed. Detailed log: `Logs/cache-cleanup-20261007.json`. No game/model/browser/Docker/personal files deleted.

## Verified in the iPhone 17 Simulator (iOS 26.0 runtime, 2026-10-07 night)

- `Tools/build-ios-simulator.sh <tag>` exports, compiles and installs the full game (BUILD SUCCEEDED, ~4.9 GB bundle incl. IDAS3 runtime pack and SoundRoom). Launch storyboards and the icon catalog compile again; the earlier workaround that stripped them was reverted after `xcrun simctl runtime match set iphoneos26.5 23A8464` fixed ibtool/actool (Xcode 26.6 ships SDK 26.5; only the 26.0 runtime is installable here).
- Metal device initializes on the simulator GPU; no shader or C# exceptions in the player log through attract, menus, story scenes and races.
- ROM gate: without a ROM the app shows ROM REQUIRED with the Documents path; after seeding `Documents/game/rom/gds-0033.chd` the startup check reports `verified CHD`.
- Touch (injected with AXe single-finger touches): OK/START, BACK, D-pad (course carousel, name-entry cursor), GAS, BRAKE and STEER. Race telemetry (`last_run.csv`) shows GAS 0→87 km/h with AT 1→3, STEER right/left turning the car in opposite directions, BRAKE 83→6 km/h.
- Full flow: attract → save select → make/model/transmission/customization → name entry → story dialogue → race (HUD, mirror, opponent panel, tachometer, minimap) → result/continue → course and opponent select. Save slot (`Documents/userdata-unity-scene/saves/slot_1`) persists across app relaunch.
- Disk: each reinstall leaves the replaced bundle in the simulator's `containermanagerd/Dead`; the build script now deletes those for this app only.

## Implemented, awaiting validation beyond the Simulator

- iOS static P/Invoke bindings; writable paths, staged runtime pack lookup, bundled ROM copy with the existing validation, and mobile touch activation.
- iOS LAN transport default; explicit Steam unavailable implementation; desktop updater/Discord disabled; Windows imports guarded.
- Separate simulator/device Unity export entry points: `Idas3IOSBuild.BuildSimulator` and `Idas3IOSBuild.BuildDevice`.
- Metal-only non-geometry-stage shader paths, native flat lighting/culling and imported paired-plane handling. `Idas3MetalGeometryChecks.Run` is the Unity test entry point.
- Android-only Gradle postprocessor guarded so the Mac need not install Android modules.
- Combined managed code compile checks were performed against matching Unity reference assemblies on Windows; these are NOT a substitute for real Mac Editor/Xcode/Metal compilation.

## Incomplete

- No device build, signing or IPA yet; iPhone 17 / iPad 9 performance, thermals and memory remain unmeasured.
- Simultaneous multi-touch (GAS + STEER at once) cannot be injected in the Simulator with AXe; it needs a real device. A complete race was not driven to the finish line.
- Touch name entry (shared with Android): the OK tap that confirms customization also enters "A" on the name screen, and one ~0.2 s D-pad tap can move the cursor two letters.
- Audio output, LAN/cross-platform interoperability and the ONLINE menu are unverified on iOS.
- Imported DDS DXT1/DXT5 textures still need an iOS-supported conversion/fallback before all imported tracks can be considered compatible.
- Native iOS document picker is not implemented; see `ios-managed-port.md` for the current file-sharing/import limitations.

## Build/test locations

- Simulator archive: `Assets/Plugins/iOS/libIdas3Unity.a` + SDK manifest.
- Device archive: `build/native-ios/device-stage/libIdas3Unity.a` + SDK manifest.
- Mac Editor plugin: `Assets/Plugins/macOS/libIdas3Unity.dylib`.
- Build logs and exit markers: `Logs/native-*`.
- `bash Tools/build-native-ios.sh iphonesimulator` stages the simulator archive.
- `IOS_PLUGIN_DIRECTORY="$PWD/build/native-ios/device-stage" bash Tools/build-native-ios.sh iphoneos` preserves simulator staging while building for devices.
- `bash Tools/run-native-ios-smoke.sh [simulator-UDID]` runs only the native ABI probe, not the game.
- `Tools/build-ios-simulator.sh <tag>` runs Unity export + xcodebuild + install; status in `Logs/ios-sim-<tag>.status`.
- The ROM is seeded outside the project at `~/InitialD-iOS-seed/rom/gds-0033.chd` (`cp -c` into the app's Documents/game/rom); never place it under Assets.

## Environment cautions for continuation

Do not copy Library/Builds/Temp/Android caches from Windows. Work from this Mac workspace. Preserve both SDK artifacts separately. Never infer iPad 9/iPhone 17 performance from Simulator timing. Logs are local; do not publish authentication URLs or account details. The temporary Unity download proxy at 127.0.0.1:17897 is an SSH forward and is not a permanent Mac network setting.
