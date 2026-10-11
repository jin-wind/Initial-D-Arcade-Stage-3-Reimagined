# Offline compiled mobile session contracts

This companion to `test_eos_mobile_compile.py` compiles the **complete, unmodified
production session**, including its actual Internet transport factory and packet
receiver. It replaces the earlier source-only factory evidence with an additional
compiled/executed check; the original narrow SDK smoke remains useful separately.

## Run

Requirements are the same as the EOS compile smoke: Python 3.11+ and an installed
.NET 8 SDK with its standard offline reference packs. First retain a successful
real-SDK smoke report, then run this test:

```sh
python Tests/Multiplayer/test_eos_mobile_compile.py \
  --sdk-source /path/to/package/Runtime/EOS_SDK \
  --sdk-archive /path/to/eos-6.2.0.tgz \
  --dotnet /path/to/dotnet --report-dir /tmp/idas3-eos-managed

python Tests/Multiplayer/test_mobile_session_contract.py \
  --eos-compile-report /tmp/idas3-eos-managed \
  --dotnet /path/to/dotnet --report-dir /tmp/idas3-session-contract
```

`--dotnet` defaults to PATH. `--report-dir` is optional; without it all output is
temporary. The runner verifies the successful source-smoke report's symbol matrix
and SDK assembly hashes, then reuses only those SDK assemblies. Archive/source
provenance is inherited from that report and explicitly recorded, not independently
re-established by this second runner. No SDK is downloaded or included in the repo.
There is no package restore, network, account or credential access.

## What is real

The session, build-key helper, platform-path/version helper, course catalog,
transport interfaces, EOS runtime and transport, car/snapshot serialization and
native bindings, record store/checks, quick-match and network impairment sources
all compile against .NET Standard 2.1 with C# 9 and warnings treated as errors.
The official EOS managed assembly is referenced for EOS-enabled configurations.
Disabled configurations have no EOS assembly reference. The small safety host
alone targets .NET 8.

The test covers:

- All 11 Android/iOS/player/Editor/disabled/stale-flag configurations from the SDK
  smoke, including actual compiled factory selection and invalid-index rejection.
  Editor cases retain Android/iOS target symbols but report the actual host
  `LinuxEditor` runtime platform. They use desktop default transport/path/version
  behavior and reject EOS configuration before native initialization. This does
  not validate macOS or Windows Editor behavior.
- A synthetic complete five-course asset fixture independently hashed by Python;
  the actual production session must compute the exact full identity and 57-byte
  matchmaking key. Every simulated input-file mutation changes both identity and
  key. Missing courses differ; an incomplete installed course fails and does not
  poison the session's compatibility cache.
- Real Android/iOS runtime path differences and canonical version behavior. The
  iOS packaged label restores the shared release identity; missing/blank labels
  fall back to the numeric app version. Changed release labels separate builds,
  while platform-specific native binary/build-ID bytes do not.
- Actual production-generated protocol-11 Hello packets, exchanged as files
  between separately compiled Android and iOS builds in both host/guest roles,
  including iOS with both opt-in flags. Matching peers reach Lobby and the host
  sends its real Lobby packet; admission alone cannot start a race.
- Rejection and cleanup for incompatible full build identities or protocols,
  zero sender nonce, an addressed Hello, trailing payload and truncated header.
- Real EOS transport rejection of missing configuration before initialization.
- No save-record writes, attempted native calls or unexpected boundary calls.

Retained evidence includes exact compile response files, per-run logs, assemblies,
synthetic inputs/generated packets and `report.json` with source/assembly hashes,
SDK provenance, identities/keys, individual checks and a final success flag. A
rerun invalidates an old success before compilation and uses fresh fixture roots.
SDK discovery has a 30-second timeout; each compiler/probe command has a 120-second
timeout. Timeouts retain captured output and a failed report. Check totals count
assertions repeated across configurations and peer roles, not distinct gameplay
scenarios.

## Explicit boundaries and limits

`SessionContractFakes.cs` substitutes Unity APIs, unrelated UI/presence/scene/music
dependencies and non-EOS transport providers. It does not substitute EOS APIs,
session logic, compatibility logic or packet/car serialization. These boundaries
throw and record accidental calls, except the small documented in-memory peer
wire, supplied Unity path/version values, clock, logging and missing-resource
behavior. Unity vector layout, math and JSON serialization are **not tested**.

Tests inject a synthetic selected car and memory transport through reflection,
then drive the session's real transport event handlers. They do not exercise the
menu/garage lifecycle or load/start a race. Native game declarations are compiled
with a deliberately forbidden library name; the .NET host additionally rejects
all game and EOS P/Invoke attempts, including attempts caught by production code.

Passing establishes the listed managed session contracts. It does **not** establish
Unity compilation/export, IL2CPP/AOT or native ABI compatibility, Unity serialization,
SDK initialization/login, actual lobby discovery or relay routing, Xcode linking,
framework embedding/signing, or device/internet crossplay. Those require the
separate real Unity/Xcode/device acceptance checks.
