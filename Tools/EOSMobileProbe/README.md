# EOS Android IL2CPP probe

A minimal Unity 6000.6.4f1 Android project for Device ID sign-in, two-member
lobby discovery and verified reliable/unreliable traffic through EOS relays.
It contains no game resources and does not implement the game transport.

Run `python Tools/EOSMobileProbe/prepare_sdk.py` before opening the project.
The script downloads official EOS Plugin 6.2.0 and checks its pinned SHA-256;
SDK files are generated inputs and are not committed. An already verified
package can be staged with `--package /path/to/package`.

Build this subproject with Unity `-buildTarget Android -executeMethod
ProbeBuild.BuildAndroid`. The APK contains ARM64 and x86_64 IL2CPP/native
libraries. It uses OpenGLES3 so the network test does not require the game's
Vulkan renderer. The APK contains no EOS product credentials.

The `EOS Android relay probe` workflow builds once and runs host/join roles
on separate GitHub-hosted Android 15 x86_64 emulators. Each uses its own
Device ID and a run-specific lobby marker. Settings are the same EOS
repository variables and secret described in [setup](../../docs/eos-setup.md).
They are passed only to the runtime test, after compilation. This does not
test ARM64 execution, iOS, real mobile carriers, or racing gameplay.

For a local emulator, provide those five `EOS_*` environment variables and run:

```text
python Tools/EOSMobileProbe/run_android.py --apk path/to/Idas3EosProbe.apk --role host --run-id unique-marker --serial emulator-5554 --report host.jsonl
```

Run the other role on a second device/emulator with the same marker. The
harness streams config to the probe's app-specific files through ADB stdin,
and the app consumes/deletes it before initializing EOS. Reports contain only
allowlisted phases, result codes and booleans. Raw SDK logs, credentials,
player IDs, caches and emulator snapshots are not uploaded.

Both roles must report `summary: Passed`, exactly two distinct authenticated
members, `RelayedConnection`, validated 100-byte payloads and acknowledgements
on both reliable and unreliable channels, a completion handshake, and cleanup.
Host destroys the lobby; guest leaves. Each test is time-bounded.
