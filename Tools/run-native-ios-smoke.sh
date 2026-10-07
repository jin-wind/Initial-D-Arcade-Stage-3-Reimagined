#!/bin/bash
# Run a small native ABI probe in an existing iPhone Simulator. No game assets.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
udid="${1:-51B6AF78-A1B6-483A-BE05-758E78300FEF}"
mkdir -p build/ios-native-smoke Logs
sdk="$(xcrun --sdk iphonesimulator --show-sdk-path)"
cat >build/ios-native-smoke/main.cpp <<'CPP'
#include "unity_bridge.h"
#include <cstdio>
int main() {
  const auto version=Idas3UnityVersion();
  std::printf("IDAS3_NATIVE_SIMULATOR_SMOKE abi=%u\n",version);
  return version==1 ? 0 : 1;
}
CPP
xcrun --sdk iphonesimulator clang++ -std=c++20 -target arm64-apple-ios16.3-simulator \
  -isysroot "$sdk" -I Native/src build/ios-native-smoke/main.cpp \
  -Xlinker -force_load -Xlinker Assets/Plugins/iOS/libIdas3Unity.a \
  -o build/ios-native-smoke/idas3-native-smoke
state="$(xcrun simctl list devices --json | python3 -c 'import sys,json; d=json.load(sys.stdin); u=sys.argv[1]; print(next(x["state"] for v in d["devices"].values() for x in v if x["udid"]==u))' "$udid")"
started=0
cleanup() { if [[ "$started" == 1 ]]; then xcrun simctl shutdown "$udid" >/dev/null 2>&1 || true; fi; }
trap cleanup EXIT
if [[ "$state" == Shutdown ]]; then xcrun simctl boot "$udid"; started=1; fi
xcrun simctl bootstatus "$udid" -b
xcrun simctl spawn "$udid" "$root/build/ios-native-smoke/idas3-native-smoke"
