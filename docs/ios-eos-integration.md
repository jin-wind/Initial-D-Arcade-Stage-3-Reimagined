# Opt-in iPhoneOS EOS integration

Base: `eos-internet`, commit `56cf108a94b5e935d1ce4846fd8e54353c16218a`.
This extends the existing iOS port; it does not replace its offline or LAN
implementation. The earlier `main` headless patch is not a prerequisite and
must not be applied wholesale to this mobile branch.

## What changed

- The iPhone EOS build uses the same Connect Device ID, lobby bucket, eight-character
  room code, forced-relay transport and game protocol as Android.
- Only initialization differs: Android retains its JNI setup and Android options;
  iPhoneOS uses the ordinary EOS `InitializeOptions` API.
- Android retains its existing `IDAS3_EOS` define. iPhoneOS opts in with the
  per-build `IDAS3_EOS_IOS` define; ordinary iOS/Simulator exports keep their
  existing provider selection. Do not put the new symbol in project settings
  or `Assets/csc.rsp`.
- Staging checks the official archive SHA-256, ARM64 Mach-O dynamic library,
  iPhoneOS platform, minimum OS, install name and binary hash. Staging does not
  read, create or send credentials and does not enable EOS by itself.
- Xcode export links the dynamic framework into UnityFramework, embeds it once
  in the app's Frameworks directory, carries the SDK notices in the app's data,
  and sets the required search paths. It is
  not force-loaded as a static archive.
- Existing Simulator and ordinary device exports exclude the staged EOS
  framework. The pinned framework has no Simulator slice. This integration
  does not claim or attempt EOS Simulator support.

## Prepare and export on a Mac

Use Unity **6000.6.4f1**, its matching iOS Build Support, Xcode, and the existing
native/data staging process in `docs/ios-port-status.md` and the mobile workflow.
Keep the release label and compatible game data the same on both phones.

1. Stage the pinned SDK without credentials:

   ```sh
   python3 Tools/prepare-eos-ios.py
   # Or reuse a downloaded official archive; its hash is still mandatory:
   python3 Tools/prepare-eos-ios.py --archive /path/to/com.playeveryware.eos-6.2.0.tgz
   ```

   This uses the same public official plugin 6.2.0 package as the Android build,
   containing EOS 1.19.2.1. It preserves the SDK license/notices. It writes ignored
   SDK/framework files and a non-secret build manifest. Python 3.11+ with tar
   extraction filters is required; Python 3.12+ is recommended.

2. For a playable Internet build, have the existing secure build process provide
   `Assets/Resources/Idas3EOS.json` with the same product, sandbox and deployment
   as Android. Its existing fields are `productId`, `sandboxId`, `deploymentId`,
   `clientId`, `clientSecret`. Use the established untrusted game-client policy;
   do not add administrative privileges. The client configuration is shipped in
   the app and is extractable. Do not paste it into tickets or commit it.

   You may omit this file for a credential-free compile/export check. The player
   then reports Internet play unavailable while offline/LAN remain usable.
   This patch did not read or verify the current product configuration.

3. Build the native archive for **iphoneos**, not iphonesimulator, and stage the
   existing game data. Use a new empty Xcode output directory:

   ```sh
   bash Tools/build-native-ios.sh iphoneos
   # Perform the existing data-staging steps before the Unity export.
   export IDAS3_IOS_OUTPUT=Builds/iOS-EOS-Device-check1
   /path/to/Unity -batchmode -quit -projectPath "$PWD" -buildTarget iOS \
     -executeMethod Idas3IOSBuild.BuildDeviceEosChecked -logFile Logs/ios-eos-export.log
   ```

   The editor menu is `Initial D > iOS > Export ARM64 Device Xcode Project with EOS`.
   `BuildDeviceEosChecked` additionally runs the existing touch/Relay/geometry checks.
   EOS exports require empty output directories to prevent stale provider payloads.
   No ROM is supplied; the existing user-supplied ROM requirement is unchanged.

4. Compile the generated Xcode project unsigned, then verify its payload:

   ```sh
   xcodebuild -project "$IDAS3_IOS_OUTPUT/Unity-iPhone.xcodeproj" \
     -target Unity-iPhone -configuration Release -sdk iphoneos -arch arm64 \
     CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO \
     "CONFIGURATION_BUILD_DIR=$PWD/Builds/iOS-EOS-Products" build
   python3 "Tools/Verify iOS App.py" Builds/iOS-EOS-Products/InitialDUnity.app \
     --sdk iphoneos --eos
   ```

   Add `--version` with the complete project release label to check the Android/iOS
   version match. The verifier requires exactly one ARM64 iPhoneOS EOS framework
   in `Frameworks/EOSSDK.framework` and the EOS build marker. Without `--eos` it
   rejects leaked EOS payloads, including in Simulator apps. It reports the final
   binary hash; signing can change that hash. This is a structural package check,
   not evidence that EOS login or racing works.

The existing `mobile-build.yml` is unchanged and does not opt iOS into EOS. Use
this explicit local export first. Signing/provisioning, SDK/service terms, app
privacy disclosures, device installation and publishing require their own
review and authorization; they have not been performed by this change.

## Offline regression checks

```sh
python3 Tests/Multiplayer/test_eos_ios_build.py
python3 Tests/Multiplayer/test_eos_mobile_compile.py --help
```

The first suite uses synthetic Mach-O fixtures to test rejection paths, staging
isolation and package checks. The second compiles the production runtime,
transport and transport selection against the real pinned EOS managed source
with small Unity-only fakes; follow [its usage guide](../Tests/Multiplayer/README-eos-mobile-compile.md)
for the local SDK path and .NET SDK. The `Mobile EOS source checks` workflow runs
these offline checks without credentials; it does not export a player.
It does not replace a Unity, IL2CPP or Xcode build. No live EOS calls occur.

## Validation still required

Existing records already cover Android/iOS Simulator LAN gameplay in both host
roles, with movement, checksums and orderly leave after the NaN-normalization
fix. That earlier result does not validate this new EOS path.

Before calling Internet crossplay working:

- Compile/export with real Unity iOS APIs; compile the Xcode iPhoneOS target
- Validate signed installation and launch on a physical iPhone
- Test Android-host/iPhone-join, then iPhone-host/Android-join, on different networks
- Check Connect login, shared room-code discovery, forced-relay connection and
  compatibility rejection against an old build
- Complete races and rematches, leave/rejoin, cancel while connecting, reject a
  third player, handle background/lock/network loss and resume
- Check larger game packets/fragmentation, longer sessions/token renewal and
  final result consistency

No physical-device, iOS native EOS, Xcode or end-to-end Internet result is
claimed by the source and offline test changes.

## References

- [Official EOS Unity iOS integration](https://dev.epicgames.com/docs/epic-online-services/game-engines/eos-unity-integration/eos-unity-integration-ios)
- [Official plugin 6.2.0 release](https://github.com/EOS-Contrib/eos_plugin_for_unity/releases/tag/v6.2.0)
- [Unity extraScriptingDefines](https://docs.unity.com/en-us/engine/6000.3/script-reference/unityeditor/buildplayeroptions)
- [Unity framework embedding API](https://docs.unity3d.com/jp/current/ScriptReference/iOS.Xcode.Extensions.PBXProjectExtensions.AddFileToEmbedFrameworks.html)
- [Existing Android/iOS LAN validation](mobile-crossplay-validation.md)
