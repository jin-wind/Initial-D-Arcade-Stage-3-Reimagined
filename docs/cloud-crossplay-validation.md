# Cloud crossplay contract validation — 2026-10-11

Base: `eos-internet` at `56cf108a94b5e935d1ce4846fd8e54353c16218a`.
This extends [the iOS EOS source validation](ios-eos-source-validation.md) with
shared-native and full managed-session checks. All results in this document
were obtained on Linux; they are not Android/iPhone device-play results.

## Verified locally

- The portable headless harness was ported onto the EOS/mobile branch without
  copying the older main-branch Windows-only plugin guard. Android still has
  its shared plugin; Apple still has its existing portable source closure.
- The production native test targets compile with GCC 14.2, CMake 4.4.4 and
  C++20. The existing `native_multiplayer` and `online_race_timeline` checks pass,
  including production packet validation and the numerical rollback matrix.
  The build retains pre-existing indentation warnings in unrelated native
  sources; this is not a warning-free whole-project build.
- All **13** harness framing, validation, timeout and cleanup unit tests pass.
- **15** two-process loopback cases pass: three car/course/condition scenarios
  (`0,3,17`) crossed with healthy, impaired, host-bound outage, client-bound
  outage and bidirectional outage. **54,000 confirmed peer-frames** match
  independently calculated clean same-binary baselines, with ordered once-only
  effects and recovery. The impairment profile is logical 200 ms RTT, jitter,
  3% loss and 2% duplicate injection at the message boundary.
- **Six synthetic CMake platform graphs** pass on the Linux host. They check
  plugin presence/type, mobile compile/link flags, Android 16 KiB alignment,
  and every compiled Apple core object being included in the plugin. **Four
  isolated mutations** confirm that the checks reject known dangerous porting
  mistakes. Synthetic branch selection is not real mobile toolchain validation.
- The **full production managed session** compiles in **11** symbol
  configurations using real EOS SDK assemblies from the prior pinned-source
  compile run. **1,021 checks**, including **four directional Android/iOS
  exchanges**, pass. Actual Hello packet serialization and admission run in
  both host/guest roles; incompatible build/protocol and malformed packets are
  rejected. Version fallback and course content fingerprint behavior use the
  real production helpers and independently calculated synthetic inputs.
- These session tests use explicit Unity/native/provider boundary fakes. They
  neither initialize native EOS/game libraries nor use an actual network,
  backend, native save store or Unity player.

Independent review covered the native harness and target graph; its identified
archive-coverage gap was fixed with direct dependency and generated-object
assertions plus a missing-link-edge mutation. Full-session review also corrected
Editor runtime-platform modeling and added bounded compiler/probe timeouts. The
final compiled-session report and all source/assembly hashes were reviewed with
no blocking findings. The tests are designed to catch
source regressions, not replace platform/device acceptance.

## Reproduce

See [headless-multiplayer.md](headless-multiplayer.md) for build/run commands and
[README-mobile-session-contract.md](../Tests/Multiplayer/README-mobile-session-contract.md)
for the retained real-SDK report prerequisite and full-session runner.
`eos-ios-source.yml` runs the managed checks; `headless-multiplayer.yml` runs
native diagnostics on Linux. They do not require user/service credentials.

For actual macOS-hosted native/Unity/IL2CPP/Xcode compilation, the separate
[GitHub Actions guide](ios-eos-github-actions.md) documents the bounded,
unsigned, credential-free EOS build workflow. It checks for at least 35 GiB
free after checkout before downloads/native compilation and uses strict macOS
file cloning for data staging. Low-space and clone failure paths were checked
with Linux fixtures; macOS/APFS execution is still unrun. Preparing or source-checking
that workflow is not evidence it has executed successfully.

## Remaining acceptance boundary

The native processes use the same Linux binary/compiler/architecture. This
proves neither ARM64/cross-compiler numerical determinism nor Android/iOS ABI
or native-EOS integration. The managed exchanges prove compatibility contracts
with synthetic content, not two installed clients joining a real room.

Actual Android APK and iPhoneOS build/link/package results, signed physical
installation, native initialization, authenticated EOS lobby/relay operation,
a complete race/finish/rematch, backgrounding/recovery and both host roles
remain separate checks. Do not claim real Android↔iPhone multiplayer from this
report or from an unsigned, service-unconfigured build alone.
