"""Stage the pinned official iPhoneOS EOS SDK, without reading credentials.

This only prepares build inputs. Use Idas3IOSBuild.BuildDeviceEosChecked to
opt in; the existing device and Simulator exports keep their prior transport.
"""
import argparse
import hashlib
import json
from pathlib import Path
import plistlib
import shutil
import struct
import tarfile
import tempfile
import urllib.request

URL = ('https://github.com/EOS-Contrib/eos_plugin_for_unity/releases/download/'
       'v6.2.0/com.playeveryware.eos-6.2.0.tgz')
SHA256 = 'aafe5a1cc278f2f65e0373777706d0a1028eea49b9548ef4a7520cc50f647c4b'
BINARY_SHA256 = '702f3e930258f06b7d55a74c91d9750f8b325fa2c2c3a656f6bcb7c2c3bc152b'
FRAMEWORK = Path('Runtime/iOS/EOSSDK.framework')


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def inspect_framework(framework):
    """Reject simulator, wrong architecture, corrupt commands and wrong SDK."""
    with (framework / 'Info.plist').open('rb') as stream:
        info = plistlib.load(stream)
    if info.get('CFBundleExecutable') != 'EOSSDK' or info.get('CFBundleSupportedPlatforms') != ['iPhoneOS']:
        raise ValueError('EOS framework must be the iPhoneOS device distribution.')
    binary = framework / 'EOSSDK'
    with binary.open('rb') as stream:
        header = stream.read(32)
        if len(header) != 32:
            raise ValueError('Truncated EOS Mach-O header.')
        magic, cpu, _, kind, count, size, _, _ = struct.unpack('<8I', header)
        if magic != 0xFEEDFACF or cpu != 0x0100000C or kind != 6:
            raise ValueError('EOS must be a thin ARM64 Mach-O dynamic library.')
        if size > 16 * 1024 * 1024:
            raise ValueError('Invalid EOS load command size.')
        commands = stream.read(size)
        if len(commands) != size:
            raise ValueError('Truncated EOS load commands.')
    offset = 0
    platforms, minimums, install_names = [], [], []
    for _ in range(count):
        if offset + 8 > size:
            raise ValueError('Truncated EOS load command.')
        command, length = struct.unpack_from('<II', commands, offset)
        if length < 8 or offset + length > size:
            raise ValueError('Invalid EOS load command length.')
        if command == 0x32:
            if length < 24:
                raise ValueError('Invalid EOS platform command.')
            platform, minimum = struct.unpack_from('<II', commands, offset + 8)
            platforms.append(platform)
            minimums.append(minimum)
        if command == 0xD:  # LC_ID_DYLIB
            if length < 24:
                raise ValueError('Invalid EOS dylib command.')
            start = struct.unpack_from('<I', commands, offset + 8)[0]
            if not 24 <= start < length:
                raise ValueError('Invalid EOS install name.')
            install_names.append(commands[offset + start:offset + length].split(b'\0')[0].decode('utf-8'))
        offset += length
    if offset != size or platforms != [2] or minimums != [15 << 16]:
        raise ValueError('Expected the pinned iPhoneOS (not Simulator) EOS SDK, minimum iOS 15.0.')
    if install_names != ['@executable_path/Frameworks/EOSSDK.framework/EOSSDK']:
        raise ValueError('Unexpected EOS framework install name.')
    if digest(binary) != BINARY_SHA256:
        raise ValueError('EOS framework SHA-256 mismatch.')
    return {'schema': 1, 'sdk': 'iphoneos', 'architectures': ['arm64'],
            'minimumOS': '15.0', 'pluginVersion': '6.2.0', 'eosVersion': '1.19.2.1',
            'archiveSha256': SHA256, 'binarySha256': BINARY_SHA256}


def stage(package, project):
    manifest = inspect_framework(package / FRAMEWORK)
    sdk = package / 'Runtime/EOS_SDK'
    if not (sdk / 'Generated/Platform/InitializeOptions.cs').is_file():
        raise ValueError('Missing EOS managed SDK.')
    target = project / 'Assets/Plugins/iOS'
    target.mkdir(parents=True, exist_ok=True)
    shutil.copytree(sdk, project / 'Assets/EOS_SDK', dirs_exist_ok=True)
    shutil.copytree(package / FRAMEWORK, target / 'EOSSDK.framework', dirs_exist_ok=True)
    # Start disabled. Only the explicit EOS device exporter enables this
    # framework. Never include a device binary in a Simulator or desktop build.
    (target / 'EOSSDK.framework.meta').write_text('''fileFormatVersion: 2
guid: c728619c8c214df9a62ec2fdcd19e72a
folderAsset: yes
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  defineConstraints: []
  isPreloaded: 0
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings: {}
  - first:
      iPhone: iOS
    second:
      enabled: 0
      settings:
        CPU: ARM64
        SDK: DeviceSDK
        AddToEmbeddedBinaries: true
  userData:
  assetBundleName:
  assetBundleVariant:
''', encoding='utf-8')
    (target / 'Idas3EosBuild.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    return manifest


def extract_and_stage(archive, project):
    if digest(archive) != SHA256:
        raise ValueError('EOS SDK archive SHA-256 mismatch.')
    with tempfile.TemporaryDirectory(prefix='idas3-eos-ios-') as temp:
        with tarfile.open(archive) as bundle:
            members = [member for member in bundle.getmembers() if
                       member.name.startswith('package/Runtime/EOS_SDK/') or
                       member.name.startswith('package/Runtime/iOS/EOSSDK.framework/')]
            bundle.extractall(temp, members=members, filter='data')
        return stage(Path(temp) / 'package', project)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project-root', type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument('--archive', type=Path, help='Previously downloaded official 6.2.0 .tgz; hash is always checked')
    args = parser.parse_args()
    if args.archive:
        extract_and_stage(args.archive, args.project_root)
    else:
        with tempfile.TemporaryDirectory(prefix='idas3-eos-download-') as temp:
            archive = Path(temp) / 'sdk.tgz'
            with urllib.request.urlopen(URL, timeout=120) as source, archive.open('wb') as target:
                shutil.copyfileobj(source, target)
            extract_and_stage(archive, args.project_root)
    print('Staged verified EOS 1.19.2.1 for ARM64 iPhoneOS. No credentials read or written.')
    print('Use Idas3IOSBuild.BuildDeviceEosChecked. Simulator EOS is not supported.')


if __name__ == '__main__':
    main()
