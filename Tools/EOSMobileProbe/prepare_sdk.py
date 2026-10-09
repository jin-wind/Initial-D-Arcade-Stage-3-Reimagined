"""Stage the pinned official SDK; no product credentials enter the Unity build."""
import argparse
import hashlib
from pathlib import Path
import shutil
import tarfile
import tempfile
import urllib.request

URL = ('https://github.com/EOS-Contrib/eos_plugin_for_unity/releases/download/'
       'v6.2.0/com.playeveryware.eos-6.2.0.tgz')
SHA256 = 'aafe5a1cc278f2f65e0373777706d0a1028eea49b9548ef4a7520cc50f647c4b'


def stage(package, project):
    shutil.copytree(package / 'Runtime/EOS_SDK', project / 'Assets/EOS_SDK', dirs_exist_ok=True)
    plugin = project / 'Assets/Plugins/Android/eos-sdk.aar'
    plugin.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(package / 'PlatformSpecificAssets~/EOS/Android/static-stdc++/aar/eos-sdk.aar', plugin)
    print('Staged EOS 6.2.0 managed SDK and Android AAR; no credentials included.')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--package', type=Path, help='Already verified local SDK package')
    args = parser.parse_args()
    project = Path(__file__).resolve().parent
    if args.package:
        stage(args.package.resolve(), project)
        return
    with tempfile.TemporaryDirectory(prefix='idas3-eos-sdk-') as temp:
        root = Path(temp)
        archive = root / 'sdk.tgz'
        with urllib.request.urlopen(URL, timeout=120) as response, archive.open('wb') as output:
            shutil.copyfileobj(response, output)
        with archive.open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != SHA256:
                raise RuntimeError('EOS SDK SHA-256 mismatch')
        with tarfile.open(archive) as bundle:
            bundle.extractall(root, filter='data')
        stage(root / 'package', project)


if __name__ == '__main__':
    main()
