"""Stage the pinned EOS SDK for Android/iPhone, or a LAN-only iOS Simulator."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import tarfile
import tempfile
import urllib.request
import urllib.error
import urllib.parse

URL = ('https://github.com/EOS-Contrib/eos_plugin_for_unity/releases/download/'
       'v6.2.0/com.playeveryware.eos-6.2.0.tgz')
SHA256 = 'aafe5a1cc278f2f65e0373777706d0a1028eea49b9548ef4a7520cc50f647c4b'
SDK_VERSION = '1.19.2.1'
DEFINES = ('-define:IDAS3_EOS', '-define:IDAS3_EOS_IOS_SIMULATOR')


def check_client(config):
    # Catch a mismatched client pair before spending time on the full APK.
    # Only send it to Epic, and never persist or print the returned token.
    basic = base64.b64encode((config['clientId'] + ':' + config['clientSecret']).encode()).decode()
    request = urllib.request.Request('https://api.epicgames.dev/auth/v1/oauth/token',
        data=urllib.parse.urlencode({'grant_type': 'client_credentials', 'deployment_id': config['deploymentId']}).encode(),
        headers={'Authorization': 'Basic ' + basic, 'Content-Type': 'application/x-www-form-urlencoded'}, method='POST')
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            token = json.loads(response.read())
            if response.status != 200 or not token.get('access_token'):
                raise RuntimeError('Epic did not return client authentication.')
            token.clear()
    except urllib.error.HTTPError as error:
        raise RuntimeError(f'Epic rejected the configured EOS client (HTTP {error.code}). Check EOS_CLIENT_ID and EOS_CLIENT_SECRET.') from None
    except urllib.error.URLError:
        raise RuntimeError('Could not reach the Epic authentication endpoint.') from None
    print('Epic accepted the configured EOS client credentials.')


def remove_generated(root, relative):
    path = root / relative
    if not path.resolve().is_relative_to(root.resolve()) or path.resolve() == root.resolve():
        raise ValueError('Generated EOS path escaped the project')
    if path.is_dir():
        shutil.rmtree(path)
    elif path.exists():
        path.unlink()


def configure_mode(root, platform):
    defines = root / 'Assets/csc.rsp'
    defines.parent.mkdir(parents=True, exist_ok=True)
    content = defines.read_text() if defines.exists() else ''
    lines = [line for line in content.splitlines() if line.strip() not in DEFINES]
    lines.append(DEFINES[1 if platform == 'iphonesimulator' else 0])
    defines.write_text('\n'.join(lines) + '\n', encoding='utf-8')
    marker = root / 'Assets/StreamingAssets/idas3-eos-build.json'
    marker.parent.mkdir(parents=True, exist_ok=True)
    marker.write_text(json.dumps({'schema': 'idas3-eos-build-v1', 'sdkVersion': SDK_VERSION,
        'platform': platform, 'transport': 'unavailable' if platform == 'iphonesimulator' else 'eos'}),
        encoding='utf-8')


def check_ios_framework(framework):
    # Reject Simulator/macOS libraries accidentally supplied as iPhone EOS.
    with (framework / 'EOSSDK').open('rb') as stream:
        header = stream.read(32)
        if len(header) != 32 or header[:4] != b'\xcf\xfa\xed\xfe' or struct.unpack_from('<I', header, 4)[0] != 0x0100000C:
            raise ValueError('EOS iPhone framework must be thin ARM64 Mach-O')
        ncmds, length = struct.unpack_from('<II', header, 16)
        if length > 16 * 1024 * 1024:
            raise ValueError('Invalid EOS framework load commands')
        commands = stream.read(length)
    platforms, offset = [], 0
    for _ in range(ncmds):
        if offset + 8 > len(commands):
            raise ValueError('Truncated EOS framework load commands')
        command, size = struct.unpack_from('<II', commands, offset)
        if size < 8 or offset + size > len(commands):
            raise ValueError('Invalid EOS framework load command size')
        if command == 0x32 and size >= 24:
            platforms.append(struct.unpack_from('<I', commands, offset + 8)[0])
        offset += size
    if offset != len(commands) or platforms != [2]:
        raise ValueError('EOS framework must target iPhoneOS, not Simulator or macOS')


def stage(package, root, platform='android'):
    if platform == 'iphonesimulator':
        # Never disguise the iPhone binary as Simulator ARM64. No native EOS
        # bindings or client credentials are needed by the unavailable adapter.
        for path in ('Assets/EOS_SDK', 'Assets/EOS_SDK.meta',
                     'Assets/Plugins/iOS/EOSSDK.framework', 'Assets/Plugins/iOS/EOSSDK.framework.meta',
                     'Assets/Resources/Idas3EOS.json', 'Assets/Resources/Idas3EOS.json.meta'):
            remove_generated(root, path)
        configure_mode(root, platform)
        return
    if platform == 'iphoneos':
        framework = package / 'Runtime/iOS/EOSSDK.framework'
        check_ios_framework(framework)
    shutil.copytree(package / 'Runtime/EOS_SDK', root / 'Assets/EOS_SDK', dirs_exist_ok=True)
    if platform == 'iphoneos':
        target = root / 'Assets/Plugins/iOS/EOSSDK.framework'
        shutil.copytree(framework, target, dirs_exist_ok=True)
        shutil.copyfile(framework.with_suffix('.framework.meta'), target.with_suffix('.framework.meta'))
    else:
        android = root / 'Assets/Plugins/Android'
        android.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(package / 'PlatformSpecificAssets~/EOS/Android/static-stdc++/aar/eos-sdk.aar', android / 'eos-sdk.aar')
        # Unity 6 needs explicit sourceSets for legacy .androidlib resources.
        dependency = Path(__file__).parent / 'EOSMobileProbe/Assets/Plugins/Android/EOS/eos_dependencies.androidlib'
        shutil.copytree(dependency, android / 'EOS/eos_dependencies.androidlib', dirs_exist_ok=True)
    configure_mode(root, platform)


def stage_archive(archive, root, platform):
    with archive.open('rb') as stream:
        if hashlib.file_digest(stream, 'sha256').hexdigest() != SHA256:
            raise RuntimeError('EOS SDK SHA-256 mismatch')
    with tempfile.TemporaryDirectory(prefix='idas3-eos-sdk-') as temp:
        with tarfile.open(archive) as bundle:
            bundle.extractall(temp, filter='data')
        stage(Path(temp) / 'package', root, platform)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--project-root', type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument('--platform', choices=('android', 'iphoneos', 'iphonesimulator'), default='android')
    source = parser.add_mutually_exclusive_group()
    source.add_argument('--package', type=Path, help='Reuse an already extracted official SDK package')
    source.add_argument('--archive', type=Path, help='Reuse a manually downloaded SDK archive, with hash verification')
    parser.add_argument('--compile-only', action='store_true', help='Stage SDK without runtime credentials')
    args = parser.parse_args()
    config = {}
    if not args.compile_only and args.platform != 'iphonesimulator':
        for field, name in [('productId', 'EOS_PRODUCT_ID'), ('sandboxId', 'EOS_SANDBOX_ID'),
                            ('deploymentId', 'EOS_DEPLOYMENT_ID'), ('clientId', 'EOS_CLIENT_ID'),
                            ('clientSecret', 'EOS_CLIENT_SECRET')]:
            value = os.environ.get(name, '').strip()
            if not value:
                raise RuntimeError('Missing EOS setting: ' + name)
            config[field] = value
        check_client(config)
    if args.platform == 'iphonesimulator':
        stage(None, args.project_root, args.platform)
    elif args.package:
        stage(args.package.resolve(), args.project_root, args.platform)
    elif args.archive:
        stage_archive(args.archive, args.project_root, args.platform)
    else:
        with tempfile.TemporaryDirectory(prefix='idas3-eos-') as temp:
            root = Path(temp)
            archive = root / 'sdk.tgz'
            with urllib.request.urlopen(URL, timeout=120) as source, archive.open('wb') as dest:
                shutil.copyfileobj(source, dest)
            stage_archive(archive, args.project_root, args.platform)
    if config:
        path = args.project_root / 'Assets/Resources/Idas3EOS.json'
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(config), encoding='utf-8')
    else:
        remove_generated(args.project_root, 'Assets/Resources/Idas3EOS.json')
        remove_generated(args.project_root, 'Assets/Resources/Idas3EOS.json.meta')
    print('EOS ' + args.platform + ' staged' + (' with game-client configuration.' if config else ' without runtime credentials.'))


if __name__ == '__main__':
    main()
