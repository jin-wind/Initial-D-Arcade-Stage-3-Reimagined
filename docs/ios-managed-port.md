# iOS managed/platform bootstrap

Work on the remote Apple Silicon Mac in `/Users/michael/Projects/InitialD-iOS`.
Keep the original Windows/Android project unchanged. This bootstrap is not a
claim that a complete iOS race or any device performance test has passed.

## Exporting

Install the project's exact Unity version and its iOS Build Support module.
Use `Tools/build-native-ios.sh iphonesimulator` for a complete ARM64 Simulator
static archive. The script must stage `Assets/Plugins/iOS/libIdas3Unity.a` and
`Idas3NativeBuild.json` with `sdk`, `architectures` (array) and `minimumOS`.
Unity menu `Initial D/iOS/Export ARM64 Simulator Xcode Project`, or batch
`-buildTarget iOS -executeMethod Idas3IOSBuild.BuildSimulator`, exports to
`Builds/iOS-Simulator`. `IDAS3_IOS_OUTPUT` overrides the output directory.
Build that Xcode project with the Simulator SDK, then install/launch with
`xcrun simctl`. Export does not automatically run Xcode or Simulator.

For a device, rebuild the archive with `iphoneos` and use
`Idas3IOSBuild.BuildDevice`. ARM64 device and ARM64 Simulator archives are
different ABIs; the build helper rejects mismatched manifests. Signing and a
device provisioning profile are separate from the unsigned simulator workflow.
Current deployment target is iOS 16.3, universal iPhone/iPad, landscape, Metal.

## Storage and input

- Read-only runtime data: `StreamingAssets/IDAS3/data` (real iOS filesystem).
- Imported course packs: `StreamingAssets/IDAS3/data/RuntimeAssets/<pack>`.
- Writable ROM: `Application.persistentDataPath/game/rom/gds-0033.chd`.
  A bundled `StreamingAssets/rom/gds-0033.chd` is seeded only when absent, then
  the existing size/SHA-256 validator decides whether startup may continue.
  The initial copy temporarily needs a second ROM's worth of free app storage.
- Saves: `Application.persistentDataPath/userdata-unity-scene`.
- Music: `Application.persistentDataPath/Custom Music`.
- Existing Android touch controls run on iOS, including safe-area layout and
  input release on focus loss/backgrounding. Simulator pointer input does not
  prove multi-finger touch usability on a physical device.

Find a simulator's app data container with:

```sh
xcrun simctl get_app_container booted com.idas3.unity.ios data
```

Unity's iOS persistent path is under that app's `Documents` directory. With the
app stopped, seed ROM/music/replay files there, then launch and validate in-game.
The Xcode postprocessor enables Files/Finder document sharing for device use.
There is **no UIDocumentPicker implementation yet**; ROM/music/replay import UI
explicitly reports this rather than calling Android Java or Windows dialogs.

## Deliberately unavailable / not validated

- Steam matchmaking and Steam Input are excluded from the iOS player; TCP
  direct-connect is the default. Cross-platform matches require actual testing.
- Desktop Discord IPC, Windows self-updater, Windows wheel force feedback,
  XInput and Windows file dialogs are unavailable. iOS has managed no-op or
  fail-closed fallbacks rather than unresolved Windows native imports.
- Desktop legacy framebuffer host is not an iOS rendering path; use the Unity
  scene host. Native archive exports and the Metal renderer must be validated
  independently of this managed patch.
- Simulator graphics, audio, storage, background/resume and a full race have
  not passed until a build is actually run. iPad 9 memory, thermals and frame
  rate and iPhone 17 performance still require real devices.
- UI safe-area/rotation, multi-touch pedals/steering, phone interruptions,
  audio routing, document import, signed-device install and LAN permission
  behavior remain explicit QA items.

API references:
- https://docs.unity.com/en-us/engine/6000.6/script-reference/unityeditor/playersettings/ios/sdkversion
- https://docs.unity3d.com/cn/6000.0/ScriptReference/PlayerSettings.iOS-simulatorSdkArchitecture.html
- https://docs.unity3d.com/ja/current/ScriptReference/AppleMobileArchitectureSimulator.html
