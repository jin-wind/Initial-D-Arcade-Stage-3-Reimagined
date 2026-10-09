"""Stage EOS for the Android game. The generated client config is not committed."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import tarfile
import tempfile
import urllib.request
import urllib.error
import urllib.parse

URL = ('https://github.com/EOS-Contrib/eos_plugin_for_unity/releases/download/'
       'v6.2.0/com.playeveryware.eos-6.2.0.tgz')
SHA256 = 'aafe5a1cc278f2f65e0373777706d0a1028eea49b9548ef4a7520cc50f647c4b'


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


def stage(package, root):
    shutil.copytree(package / 'Runtime/EOS_SDK', root / 'Assets/EOS_SDK', dirs_exist_ok=True)
    android = root / 'Assets/Plugins/Android'
    android.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(package / 'PlatformSpecificAssets~/EOS/Android/static-stdc++/aar/eos-sdk.aar', android / 'eos-sdk.aar')
    # Unity 6 needs explicit sourceSets for legacy .androidlib resources.
    dependency = Path(__file__).parent / 'EOSMobileProbe/Assets/Plugins/Android/EOS/eos_dependencies.androidlib'
    shutil.copytree(dependency, android / 'EOS/eos_dependencies.androidlib', dirs_exist_ok=True)
    defines = root / 'Assets/csc.rsp'
    content = defines.read_text() if defines.exists() else ''
    if '-define:IDAS3_EOS' not in content:
        defines.write_text(content.rstrip() + '\n-define:IDAS3_EOS\n')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--project-root', type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument('--package', type=Path)
    parser.add_argument('--compile-only', action='store_true', help='Stage SDK without runtime credentials')
    args = parser.parse_args()
    config = {}
    if not args.compile_only:
        for field, name in [('productId', 'EOS_PRODUCT_ID'), ('sandboxId', 'EOS_SANDBOX_ID'),
                            ('deploymentId', 'EOS_DEPLOYMENT_ID'), ('clientId', 'EOS_CLIENT_ID'),
                            ('clientSecret', 'EOS_CLIENT_SECRET')]:
            value = os.environ.get(name, '').strip()
            if not value:
                raise RuntimeError('Missing EOS setting: ' + name)
            config[field] = value
        check_client(config)
    if args.package:
        stage(args.package.resolve(), args.project_root)
    else:
        with tempfile.TemporaryDirectory(prefix='idas3-eos-') as temp:
            root = Path(temp)
            archive = root / 'sdk.tgz'
            with urllib.request.urlopen(URL, timeout=120) as source, archive.open('wb') as dest:
                shutil.copyfileobj(source, dest)
            with archive.open('rb') as stream:
                if hashlib.file_digest(stream, 'sha256').hexdigest() != SHA256:
                    raise RuntimeError('EOS SDK SHA-256 mismatch')
            with tarfile.open(archive) as bundle:
                bundle.extractall(root, filter='data')
            stage(root / 'package', args.project_root)
    if config:
        path = args.project_root / 'Assets/Resources/Idas3EOS.json'
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(config), encoding='utf-8')
    print('EOS Android SDK staged' + (' with game-client configuration.' if config else ' for compilation only.'))


if __name__ == '__main__':
    main()
