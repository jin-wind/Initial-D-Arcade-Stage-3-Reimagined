#!/bin/bash
# Build on an Apple Silicon Mac; invoke with bash, no executable bit required.
#   bash Tools/build-native-ios.sh iphonesimulator
#   bash Tools/build-native-ios.sh iphoneos
# CMAKE_GENERATOR='Unix Makefiles' is also supported. No Ninja is required.
set -euo pipefail
sdk="${1:-iphonesimulator}"
case "$sdk" in
  iphoneos) target_suffix="" ;;
  iphonesimulator) target_suffix="-simulator" ;;
  *) echo "Usage: bash $0 [iphonesimulator|iphoneos]" >&2; exit 2 ;;
esac
if [[ $# -gt 1 || "$(uname -s)" != Darwin ]]; then
  echo "Run this script on macOS, with at most one SDK argument." >&2
  exit 2
fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${CONFIGURATION:-Release}"
generator="${CMAKE_GENERATOR:-Xcode}"
architectures="${IOS_ARCHITECTURES:-arm64}"
minimum_os="${IOS_DEPLOYMENT_TARGET:-16.3}"
case "$generator" in
  Xcode|'Unix Makefiles') ;;
  *) echo "CMAKE_GENERATOR must be Xcode or Unix Makefiles." >&2; exit 2 ;;
esac
case "$configuration" in
  Debug|Release|RelWithDebInfo|MinSizeRel) ;;
  *) echo "Unsupported CONFIGURATION: $configuration" >&2; exit 2 ;;
esac
if [[ ! "$minimum_os" =~ ^[0-9]+([.][0-9]+){0,2}$ ]]; then
  echo "Invalid IOS_DEPLOYMENT_TARGET: $minimum_os" >&2; exit 2
fi
IFS=';' read -r -a arch_list <<< "$architectures"
for arch in "${arch_list[@]}"; do
  case "$sdk:$arch" in
    iphoneos:arm64|iphonesimulator:arm64|iphonesimulator:x86_64) ;;
    *) echo "Unsupported architecture $arch for $sdk" >&2; exit 2 ;;
  esac
done
cmake_command="${CMAKE_COMMAND:-cmake}"
if ! command -v "$cmake_command" >/dev/null 2>&1; then
  echo "CMake 3.24+ is required; set CMAKE_COMMAND to its absolute path." >&2
  exit 1
fi
sdk_path="$(xcrun --sdk "$sdk" --show-sdk-path)"
# SDK, architecture, configuration and generator get isolated build trees.
# A device arm64 archive is NOT interchangeable with a simulator arm64 archive.
build_dir="${BUILD_DIRECTORY:-$root/build/native-ios/$sdk/${architectures//;/_}/${generator// /-}/$configuration}"
artifact_dir="$build_dir/Artifacts"
stage_dir="${IOS_PLUGIN_DIRECTORY:-$root/Assets/Plugins/iOS}"
"$cmake_command" -S "$root/Native" -B "$build_dir" -G "$generator" \
  -DCMAKE_SYSTEM_NAME=iOS \
  "-DCMAKE_OSX_SYSROOT=$sdk_path" \
  "-DCMAKE_OSX_ARCHITECTURES=$architectures" \
  "-DCMAKE_OSX_DEPLOYMENT_TARGET=$minimum_os" \
  "-DCMAKE_BUILD_TYPE=$configuration" \
  -DCMAKE_TRY_COMPILE_TARGET_TYPE=STATIC_LIBRARY \
  -DCMAKE_XCODE_ATTRIBUTE_CODE_SIGNING_ALLOWED=NO \
  -DCMAKE_XCODE_ATTRIBUTE_CODE_SIGNING_REQUIRED=NO \
  -DCMAKE_XCODE_ATTRIBUTE_ENABLE_BITCODE=NO \
  "-DIDAS3_IOS_PLUGIN_OUTPUT_DIRECTORY=$artifact_dir"
"$cmake_command" --build "$build_dir" --config "$configuration" \
  --target Idas3Unity --parallel "${JOBS:-$(sysctl -n hw.ncpu)}"
archive="$artifact_dir/$configuration/libIdas3Unity.a"
[[ -s "$archive" ]] || { echo "Archive not produced: $archive" >&2; exit 1; }
# Static archiving alone does not diagnose missing translation units. Force-load
# every object into a disposable, unsigned link probe before staging the plugin.
# This is only a link check; it is not an iOS app and is not run on a device.
printf 'int main() { return 0; }\n' > "$build_dir/idas3_archive_link_check.cpp"
for arch in "${arch_list[@]}"; do
  xcrun --sdk "$sdk" clang++ -std=c++20 \
    -target "${arch}-apple-ios${minimum_os}${target_suffix}" \
    -isysroot "$sdk_path" "$build_dir/idas3_archive_link_check.cpp" \
    -Xlinker -force_load -Xlinker "$archive" \
    -o "$build_dir/idas3_archive_link_check-$arch"
done
mkdir -p "$stage_dir"
# Keep both SDK builds in build/native-ios; select exactly one Unity .a at a time.
# Do not copy both platform archives into Assets: they have identical symbols.
cp "$archive" "$stage_dir/libIdas3Unity.a"
cat > "$stage_dir/Idas3NativeBuild.json" <<EOF
{
  "sdk": "$sdk",
  "architectures": ["${architectures//;/\", \"}"],
  "minimumOS": "$minimum_os",
  "configuration": "$configuration",
  "archive": "libIdas3Unity.a"
}
EOF
printf '\nStaged %s archive: %s/libIdas3Unity.a\n' "$sdk" "$stage_dir"
printf 'Set the Unity iOS target SDK to match: %s\n' "$sdk"
