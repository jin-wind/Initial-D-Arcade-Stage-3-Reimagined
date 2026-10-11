# Headless multiplayer verification

The shared native multiplayer core can be tested with two independent processes
on Linux or Windows without Unity, a GPU, a mobile emulator or EOS. This is a
diagnostic harness, not a playable build or Android/iOS crossplay demonstration.
The Android and Apple portable plugin branches remain separate and unchanged;
this harness is not added to mobile cross-builds.

## Build

Requirements: CMake 3.24+, a C++20 compiler, Python 3.10+, and this checkout's
`Native/data`. No original ROM is downloaded, extracted or required by these
numerical tests. Do not point any test at a player's saves.

From the repository root:

```sh
cmake -S Native -B Native/build-headless -DCMAKE_BUILD_TYPE=Release
cmake --build Native/build-headless --target headless_multiplayer_checks --parallel 2
ctest --test-dir Native/build-headless -R '^(native_multiplayer|online_race_timeline)$' --output-on-failure --timeout 900
python3 -m unittest discover -s Tests/Multiplayer -p test_headless_peers_unit.py -v
python3 Tests/Multiplayer/test_headless_peers.py --peer Native/bin/headless_online_peer --root Native --output Verification/headless-multiplayer
```

With a multi-configuration generator such as Visual Studio, add `--config Release`
to `cmake --build` and `-C Release` to `ctest`, and pass its actual executable path
(usually `Native/bin/Release/headless_online_peer.exe`).
`CMAKE_RUNTIME_OUTPUT_DIRECTORY` can place the binaries elsewhere. Build the named
targets above: this does not make every historical native renderer test portable.

`--output` must name a new directory, so use a fresh name for each run. Omit it
to create a temporary report directory automatically; the runner prints its path.
The report includes each process ID, executable hash, confirmed state/effect
hashes, actual impairment counts, exit codes and failure details. Peer logs are
kept beside `report.json`, including on failure. Each case has a 120-second
default timeout and child processes are reaped on completion or interruption.

The default runs 1,800 frames per peer for healthy delivery, impairment, loss
toward the host, loss toward the client and loss in both directions. Use
`--scenarios 0,3,17` for additional car/course/condition combinations,
`--imported RuntimeAssets/TSUBAKI` for an imported pack, or `--timeout 180` on a
slower machine. `--help` lists delay, loss and seed controls. Impairment timing
uses logical ticks, so changing CPU speed does not change the network schedule.

When Python 3.10+ is found by CMake, the same default loopback suite is also
registered as `headless_online_peers` in CTest and can be run repeatedly without
choosing report directories. `headless_peer_harness` covers framing, validation,
startup failure, timeout and child-process cleanup independently of game assets.

## What is tested

- `native_multiplayer_tests`: production configuration/snapshot ABI validation,
  supported cars/courses, invalid fields and non-finite input rejection.
- `online_race_timeline_tests`: production simulation and rollback timeline,
  malformed/foreign packets, confirmed state hashes, exactly-once effects,
  collisions and recovery. Its existing matrix uses 18 scenarios and 50/100/200/250
  ms simulated RTT, including jitter, loss and duplicates.
- `test_headless_peers.py`: two actual native child processes with their own
  simulation, memory and input history. A Python coordinator relays unmodified
  `OnlineRaceLink` packets through real loopback TCP sockets. Deterministic
  application-level impairment exercises retransmission and bounded-history
  stalls. Both peers compare every confirmed frame and contact-effect digest
  with independently generated clean baselines and must agree at the end.

The coordinator binds only `127.0.0.1` on an OS-assigned port. It does not connect
to public game services. Delays, drops and duplicates are injected at the
application-message boundary; they are not a measurement of operating-system TCP
packet loss, real-time internet latency or bandwidth. The test protocol belongs
only to this diagnostic harness and cannot join a game lobby.

## What remains outside this test

The harness does not run the Unity player, production C# session/TCP adapter,
Steam/EOS lobbies or relay, menus, rendering, device input, award persistence, or
post-race rematch UI. It does not prove determinism across different compilers,
architectures or machines. Those still require real platform builds and separate clients. Android/iOS
acceptance specifically needs signed physical-device builds joining the same EOS
room with compatible content, racing, reconnecting and returning to the lobby.
A successful host-only test does not establish ARM64 determinism or live EOS
behavior.

The Windows tests in `Tests/Multiplayer/test_network_recovery.py` exercise two
actual native scene DLLs. `Idas3MultiplayerSmoke` can exercise real Unity clients
through Direct LAN. Keep those checks separate from this headless core result.

## Automation

`.github/workflows/headless-multiplayer.yml` builds only the portable targets,
runs the numerical matrix and separate-process suite, and retains diagnostic
reports on both success and failure. Adding the workflow locally does not run it
on GitHub; it runs after an authorized push or manual dispatch.
Artifacts retain only explicit JSON reports and test/configure logs for seven
days. Mutation fixtures link to the native source and game data, so their full
directories must not be uploaded.

## Mobile CMake regression boundary

On a Linux host, `python3 Tests/Multiplayer/test_native_target_graph.py` evaluates
the real CMake source in six configurations using CMake's File API. Android,
iPhoneOS, macOS and Windows branches are deliberately synthetic: the installed
Linux compiler is retained. This catches missing plugin targets, wrong library
kinds, Windows-link leakage, missing strict-float flags, missing Android 16 KiB
alignment, and accidental mobile inclusion of the diagnostic executable. It is
not a mobile configure/build/link test and requires no downloaded SDK.

The iPhoneOS branch must retain the complete static archive built from OBJECT
libraries; Android retains its shared plugin. Linux has the diagnostic targets
and no unsupported Unity plugin. A cross-compiling host has no runnable headless
target. This prevents a tempting `if(WIN32)` wrapper around the plugin include
from silently deleting Android support when porting this harness.

Use `--cmake /path/to/cmake` if CMake is not on PATH and `--report-dir NEW_PATH`
to retain generated target-graph evidence. Four isolated source mutations also
prove that the checker rejects the Windows-only plugin gate, wrong Apple core
library kind, missing Apple object-library dependency, and Android diagnostic
leakage. CTest also registers this check as
`native_platform_target_graph` on Linux.
