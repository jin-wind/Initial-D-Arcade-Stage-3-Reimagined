# iOS EOS source validation — 2026-10-11

Baseline: `eos-internet` at `56cf108a94b5e935d1ce4846fd8e54353c16218a`.
All checks below ran locally on Linux. No repository push, service calls,
credentials, signing, installation or deployment were performed.

## Passed

- Downloaded the public official plugin **6.2.0** archive already pinned by the
  Android script. Its SHA-256 matched
  `aafe5a1cc278f2f65e0373777706d0a1028eea49b9548ef4a7520cc50f647c4b`.
- Staged its actual iPhoneOS framework and real managed SDK with
  `Tools/prepare-eos-ios.py --archive ...`. No synthetic framework was used for
  this staging check. Framework SHA-256:
  `702f3e930258f06b7d55a74c91d9750f8b325fa2c2c3a656f6bcb7c2c3bc152b`.
  Inspection confirmed ARM64 dynamic Mach-O, iPhoneOS platform 2, minimum iOS
  15.0 and install name `@executable_path/Frameworks/EOSSDK.framework/EOSSDK`.
- **20 offline staging/package tests**, including malformed/truncated/wrong
  platform/wrong architecture/wrong hash inputs, preservation of Android and
  existing config, default-disabled framework import, Simulator/ordinary payload
  rejection, duplicate framework, missing manifest and missing SDK notice.
  These rejection-path tests use synthetic fixtures, not an actual exported app.
- **11 managed compile configurations**, using all **1,083 unmodified official
  EOS C# sources**, byte-matched to the pinned archive, C# 9 and .NET Standard 2.1
  references. Compiled production runtime, transport and interfaces; no mocked
  EOS APIs. Minimal Unity boundary fakes are explicit test-only files.
- **80 safe configuration/metadata probes**: provider type presence, actual SDK
  native-library import names, missing/malformed/incomplete config rejection,
  Editor rejection and synthetic mobile config acceptance. No native calls.
- **11 source-only session factory checks** for the same flag matrix. These are
  preprocessor checks, not full session/UI compilation.
- All compiler logs were empty: no warnings or errors. Python syntax, workflow
  YAML parsing and `git diff --check` passed.
- Independent source review completed. No blocking issue found in the Android
  preservation, iOS opt-in path, SDK staging, simulator exclusion or embedding
  code. This is not a substitute for running the real exporter.

The matrix includes Android EOS; iOS EOS; iOS with both flags; Android/iOS-target
Linux Editor; plain Android/iOS; opposite-platform flags; stale desktop flags;
and plain Editor. The plain iOS symbol case is the one used by ordinary and
Simulator exports, but this check does not itself run those exporters.

## Not run / still required

- Unity Editor/API compilation, IL2CPP/AOT conversion or Xcode export
- Actual Xcode linking, embedding, app-package verification or launch
- Signed physical iPhone build, Android/iPhone EOS room discovery or gameplay
- Full race/finish/rematch, device backgrounding, carrier networks or token renewal
- New hosted CI run: the source workflow is supplied but has not been pushed/run

The existing Android/iOS Simulator LAN evidence in
[mobile-crossplay-validation.md](mobile-crossplay-validation.md) remains valid
historical context and was not rerun here. It does not establish this EOS path.

## Reproduce

See [the integration guide](ios-eos-integration.md) for SDK staging, the explicit
`BuildDeviceEosChecked` exporter and `Verify iOS App.py --eos`. See
[the managed test guide](../Tests/Multiplayer/README-eos-mobile-compile.md) for
its exact inputs and limits. The deliverable includes the machine-readable
managed report and the staging-test log, without proprietary SDK binaries.

## Additional cloud regression coverage

The subsequent [cloud contract validation](cloud-crossplay-validation.md) adds
full compiled-session/Hello exchange tests, two-process native coverage and
mobile CMake preservation checks. The source-only session factory check above
is retained as a narrow earlier check; it is no longer the only session evidence.
The [iOS Actions workflow](ios-eos-github-actions.md) is separately prepared for
a real unsigned device build. This report does not claim a hosted run succeeded.
