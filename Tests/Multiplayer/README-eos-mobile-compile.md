# Mobile EOS managed compile smoke

This opt-in test compiles the **unmodified official EOS managed SDK** and the
production `Idas3EosRuntime.cs`, `Idas3EosTransport.cs`, and transport interfaces.
It does not replace EOS types or use a mocked EOS assembly. The only substitutions
are the explicitly labeled Unity boundary in `EosCompileUnityFakes.cs`.

Requirements: Python 3.11+ and an installed .NET 8 SDK (including its standard
`NETStandard.Library.Ref/2.1.0` and `Microsoft.NETCore.App.Ref` packs). The runner
uses the SDK's Roslyn compiler directly; it does not restore packages, download
files, read credentials or contact EOS. The production and vendor code compile
against .NET Standard 2.1 references, with C# 9 syntax. The host-side metadata
probe alone uses .NET 8.

Obtain the EOS package pinned by `Tools/prepare-eos-android.py` through the existing
SDK preparation process. Pass its extracted `package/Runtime/EOS_SDK` directory
(or the staged `Assets/EOS_SDK` directory). Passing the original `.tgz` additionally
verifies the repository's archive SHA-256 pin and every compiled SDK source byte.
No SDK sources or binaries are bundled with this test.

```sh
python Tests/Multiplayer/test_eos_mobile_compile.py \
  --sdk-source /path/to/package/Runtime/EOS_SDK \
  --sdk-archive /path/to/com.playeveryware.eos-6.2.0.tgz \
  --dotnet /path/to/dotnet \
  --report-dir /tmp/idas3-eos-compile
```

`--dotnet` defaults to `dotnet` on PATH. Without `--report-dir`, output is temporary
and removed after a successful run. A retained report directory contains compiler
response files/logs, assemblies, probe logs and `report.json` with hashes of the
production sources, compiled SDK sources, and output assemblies. Supply an empty
or purpose-specific report directory.

## Coverage

- Android EOS, iOS EOS and iOS with both opt-in flags compile against all real SDK
  source files. The iOS fake deliberately has no Android JNI classes, so leaked
  Android initialization fails compilation.
- Android-target and iOS-target Editor cases also compile the actual Editor SDK
  branch. The harness uses Linux Editor symbols; this is not macOS/Windows Editor
  validation. Existing Editor selection semantics are retained, with configuration
  rejected before any resource load or native initialization.
- Plain Android, plain iOS (the symbol state used by simulator/non-EOS export),
  wrong-platform opt-in flags, desktop with stale flags and ordinary Editor compile
  **without any EOS assembly reference**, and the runtime/transport types must be
  absent. This checks symbols, not the iOS exporter itself.
- Safe reflection probes inspect compiled type presence and the real SDK library
  constant and player `EOS_Initialize` import metadata. They exercise configuration
  rejection for missing/malformed/incomplete data, Editor and desktop; a synthetic
  complete config enables only the player case. All values are invented. There is
  no real JSON parsing in the fake and this is not a Unity serialization test.
- A separate, explicitly source-only preprocessor check inspects the real session
  factory's Internet case for every symbol set, requiring EOS or Unity Relay as
  appropriate. The full session and non-EOS transport implementations are not
  compiled by this narrow test.

The fakes throw if runtime initialization, Android JNI or filesystem-path access
is attempted. The probe never calls EOS initialization/authentication/native
methods, opens a room or sends a packet. Passing proves managed API/signature and
conditional-compilation compatibility plus the listed safe checks. It does **not**
prove a Unity build, IL2CPP/AOT conversion, Xcode link/export, framework embedding,
signing, device execution, cross-device lobby discovery or internet relay play.
Those remain separate real Unity/Xcode/device acceptance steps.
