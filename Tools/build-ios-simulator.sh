#!/bin/bash
# Export the Unity iOS Simulator Xcode project, compile it and install it on the
# booted simulator. Run on the Mac. Usage: Tools/build-ios-simulator.sh <log-tag>
#
# Xcode 26.6 ships the iOS 26.5 SDK but only the iOS 26.0 simulator runtime can
# be installed here; ibtool/actool then fail with "No simulator runtime version
# from [...] available". Fix once per Mac:
#   xcrun simctl runtime match set iphoneos26.5 23A8464
set -uo pipefail
cd "$(dirname "$0")/.."
tag=${1:?usage: Tools/build-ios-simulator.sh <log-tag>}
editor=${UNITY_EDITOR:-$HOME/Applications/UnityEditors/6000.6.4f1/Unity.app/Contents/MacOS/Unity}
app=Builds/iOS-Simulator/build/Debug-iphonesimulator/InitialDUnity.app
log=Logs/ios-sim-$tag
status() { echo "$1" >>"$log.status"; echo "$1"; }
free_gib() { df -k /System/Volumes/Data | awk 'NR==2{printf "%d", $4/1048576}'; }
: >"$log.status"

"$editor" -batchmode -projectPath "$PWD" -buildTarget iOS -force-metal -job-worker-count 4 \
    -executeMethod Idas3IOSBuild.BuildSimulator -logFile "$PWD/$log-export.log" -quit
code=$?; status "export $code"; [ $code = 0 ] || exit $code

# The export's postprocess clones the ~5 GiB Data folder into the app instead
# of copying it, so a full Xcode build mostly costs ~1-2 GiB of intermediates.
rm -rf "$app"
if [ "$(free_gib)" -lt 4 ]; then status "xcode skipped: only $(free_gib) GiB free"; exit 28; fi
xcodebuild -project Builds/iOS-Simulator/Unity-iPhone.xcodeproj -target Unity-iPhone \
    -configuration Debug -sdk iphonesimulator -arch arm64 build >"$log-xcode.log" 2>&1 &
build=$!
# Never let a build fill the user's disk: stop below 1.5 GiB free.
while kill -0 $build 2>/dev/null; do
    if [ "$(df -k /System/Volumes/Data | awk 'NR==2{print int($4/1024)}')" -lt 1536 ]; then
        kill $build; wait $build; rm -rf "$app"
        status "xcode aborted: disk nearly full (free $(free_gib) GiB)"; exit 28
    fi
    sleep 5
done
wait $build
code=$?; status "xcode $code"; [ $code = 0 ] || exit $code

xcrun simctl install booted "$app"
code=$?
# The simulator parks each replaced ~5 GiB bundle under containermanagerd/Dead
# and may not purge it for hours; on this disk that fills the volume quickly.
dead="$(xcrun simctl getenv booted HOME)/Library/Caches/com.apple.containermanagerd/Dead"
for stale in "$dead"/temp.*/*/InitialDUnity.app; do
    [ -d "$stale" ] && rm -rf "$(dirname "$stale")"
done
# Keep the simulator visible on the Mac desktop; a device booted with simctl
# alone runs headless.
open -a "$(xcode-select -p)/Applications/Simulator.app"
status "install $code (free $(free_gib) GiB)"; exit $code
