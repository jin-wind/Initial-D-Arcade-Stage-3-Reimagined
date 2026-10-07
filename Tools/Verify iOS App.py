"""Verify an extracted ARM64 iOS app; device signing is a separate step."""
import argparse
import hashlib
import json
import plistlib
import re
import struct
from pathlib import Path


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            h.update(chunk)
    return h.hexdigest()


def macho_platform(path):
    with path.open("rb") as stream:
        header = stream.read(32)
        if len(header) != 32 or header[:4] != b"\xcf\xfa\xed\xfe":
            raise ValueError(f"Expected thin little-endian Mach-O64: {path}")
        if struct.unpack_from("<I", header, 4)[0] != 0x0100000C:
            raise ValueError(f"Expected ARM64 Mach-O: {path}")
        ncmds, cmd_bytes = struct.unpack_from("<II", header, 16)
        if cmd_bytes > 16 * 1024 * 1024:
            raise ValueError("Unreasonable Mach-O load command size")
        commands = stream.read(cmd_bytes)
    offset = 0
    platforms = []
    for _ in range(ncmds):
        if offset + 8 > len(commands):
            raise ValueError("Truncated Mach-O load commands")
        command, size = struct.unpack_from("<II", commands, offset)
        if size < 8 or offset + size > len(commands):
            raise ValueError("Invalid Mach-O load command length")
        if command == 0x32:  # LC_BUILD_VERSION: iOS=2, iOS Simulator=7.
            if size < 24:
                raise ValueError("Invalid LC_BUILD_VERSION")
            platforms.append(struct.unpack_from("<I", commands, offset + 8)[0])
        offset += size
    if offset != len(commands) or len(platforms) != 1:
        raise ValueError("Expected one platform build command")
    return platforms[0]


def verify(app, sdk, expected_version=None):
    app = app.resolve(strict=True)
    report = {"app": str(app), "sdk": sdk, "errors": []}
    errors = report["errors"]
    with (app / "Info.plist").open("rb") as stream:
        info = plistlib.load(stream)
    report["bundle_id"] = info.get("CFBundleIdentifier")
    report["bundle_version"] = info.get("CFBundleShortVersionString")
    report["build_number"] = info.get("CFBundleVersion")
    if report["bundle_id"] != "com.idas3.unity.ios":
        errors.append("Unexpected bundle identifier")
    expected_platform = 7 if sdk == "iphonesimulator" else 2
    report["binaries"] = {}
    executable = info.get("CFBundleExecutable", "")
    if not executable or Path(executable).name != executable:
        raise ValueError("Invalid CFBundleExecutable")
    for binary in (app / executable, app / "Frameworks/UnityFramework.framework/UnityFramework"):
        platform = macho_platform(binary)
        report["binaries"][str(binary.relative_to(app))] = {
            "platform": platform, "sha256": digest(binary)
        }
        if platform != expected_platform:
            errors.append(f"SDK mismatch: {binary.name} platform {platform}")
    raw = app / "Data/Raw"
    report["game_version"] = (raw / "idas3-app-version.txt").read_text().strip()
    numeric = re.match(r"^\d+(?:\.\d+){0,2}", report["game_version"])
    build = re.search(r"(\d+)$", report["game_version"][numeric.end():]) if numeric else None
    if not numeric or numeric.group() != report["bundle_version"]:
        errors.append("Numeric iOS bundle version does not match the game version")
    if str(report["build_number"]) != (build.group(1) if build else "1"):
        errors.append("iOS build number does not match the game version")
    if expected_version is not None and report["game_version"] != expected_version:
        errors.append("Game release label differs from the source project version")
    native = json.loads((raw / "ios-native-build-id.json").read_text())
    if native.get("sdk") != sdk or native.get("architectures") != ["arm64"]:
        errors.append("Native archive manifest SDK/architecture mismatch")
    manifest = json.loads((raw / "IDAS3/data.manifest.json").read_text())
    entries = manifest.get("files", [])
    if manifest.get("schema") != "idas3-unity-runtime-data-v1" or not entries:
        raise ValueError("Missing or incompatible runtime manifest")
    data_root = (raw / "IDAS3/data").resolve(strict=True)
    seen = set()
    total = 0
    for i, entry in enumerate(entries):
        name = entry["path"]
        if not isinstance(name, str) or not name or "\\" in name or ":" in name or "\0" in name:
            raise ValueError("Invalid runtime manifest path")
        if any(part in ("", ".", "..") for part in name.split("/")) or name in seen:
            raise ValueError("Invalid or duplicate runtime manifest path")
        seen.add(name)
        path = (data_root / name).resolve()
        if not path.is_relative_to(data_root):
            raise ValueError("Runtime manifest escaped its data directory")
        if not path.is_file() or path.stat().st_size != entry["bytes"]:
            errors.append("Missing or wrong size: " + name)
        elif digest(path).lower() != entry["sha256"].lower():
            errors.append("SHA-256 mismatch: " + name)
        total += entry["bytes"]
        if (i + 1) % 2000 == 0:
            print(f"Verified {i + 1}/{len(entries)} runtime files", flush=True)
    if any(p.suffix.lower() in (".chd", ".cue") or p.name.lower().startswith("gds-0033")
           for p in raw.rglob("*") if p.is_file()):
        errors.append("ROM found in CI app")
    report["runtime_files_verified"] = len(entries)
    report["runtime_bytes"] = total
    report["passed"] = not errors
    output = app.with_suffix(".verification.json")
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2), flush=True)
    return report["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path)
    parser.add_argument("--sdk", choices=("iphoneos", "iphonesimulator"), required=True)
    parser.add_argument("--version", help="Full release label expected by Android peers")
    args = parser.parse_args()
    raise SystemExit(0 if verify(args.app, args.sdk, args.version) else 1)
