"""Compile production mobile EOS sources against the real pinned managed SDK.

Requires Python 3.11+ and the .NET 8 SDK; no Unity, native EOS binaries, credentials,
NuGet restore, or network access is used. See README-eos-mobile-compile.md.
"""
import argparse
import ast
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[2]
TESTS = Path(__file__).resolve().parent
SOURCES = [ROOT / 'Assets/Scripts/Multiplayer' / name for name in (
    'Idas3MultiplayerTypes.cs', 'Idas3EosRuntime.cs', 'Idas3EosTransport.cs')]
CASES = [
    ('android', ['IDAS3_EOS', 'UNITY_ANDROID'], True, 'EOSSDK', 'Android'),
    ('ios', ['IDAS3_EOS_IOS', 'UNITY_IOS'], True, 'EOSSDK.framework/EOSSDK', 'IPhonePlayer'),
    ('ios_both_flags', ['IDAS3_EOS', 'IDAS3_EOS_IOS', 'UNITY_IOS'], True, 'EOSSDK.framework/EOSSDK', 'IPhonePlayer'),
    ('android_editor', ['IDAS3_EOS', 'UNITY_ANDROID', 'UNITY_EDITOR', 'UNITY_EDITOR_LINUX'], True, 'libEOSSDK-Linux-Shipping', 'Android'),
    ('ios_editor', ['IDAS3_EOS_IOS', 'UNITY_IOS', 'UNITY_EDITOR', 'UNITY_EDITOR_LINUX'], True, 'libEOSSDK-Linux-Shipping', 'IPhonePlayer'),
    ('android_no_eos', ['UNITY_ANDROID'], False, '', ''),
    ('ios_no_eos_or_simulator', ['UNITY_IOS'], False, '', ''),
    ('ios_android_flag_only', ['IDAS3_EOS', 'UNITY_IOS'], False, '', ''),
    ('android_ios_flag_only', ['IDAS3_EOS_IOS', 'UNITY_ANDROID'], False, '', ''),
    ('desktop_stale_flags', ['IDAS3_EOS', 'IDAS3_EOS_IOS', 'UNITY_STANDALONE_WIN'], False, '', ''),
    ('editor_no_eos', ['UNITY_EDITOR', 'UNITY_EDITOR_LINUX'], False, '', ''),
]


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def selector_check(symbols, expected):
    """Source-only check of the existing factory's Internet case, not Unity QA."""
    text = (ROOT / 'Assets/Scripts/Multiplayer/Idas3MultiplayerSession.cs').read_text(encoding='utf-8')
    text = text.split('case InternetTransport:', 1)[1].split('default:', 1)[0]
    active = True
    stack = []
    selected = []
    for line in text.splitlines():
        line = line.strip()
        if line.startswith('#if '):
            expression = line[4:]
            tokens = re.findall(r'&&|\|\||!|\(|\)|[A-Za-z_][A-Za-z_0-9]*', expression)
            if ''.join(tokens) != re.sub(r'\s+', '', expression):
                raise AssertionError('Unsupported preprocessor expression: ' + expression)
            translated = ' '.join({'&&': 'and', '||': 'or', '!': 'not', '(': '(', ')': ')',
                                  'true': 'True', 'false': 'False'}.get(token, str(token in symbols)) for token in tokens)
            # Tokens above allow only boolean operators, parentheses and constants.
            condition = bool(eval(translated, {'__builtins__': {}}, {}))
            stack.append((active, condition))
            active = active and condition
        elif line == '#else':
            parent, condition = stack[-1]
            active = parent and not condition
        elif line == '#endif':
            active, _ = stack.pop()
        elif line.startswith('#'):
            raise AssertionError('Unsupported factory preprocessor directive: ' + line)
        elif active:
            selected.append(line)
    assert not stack, 'Unbalanced factory directives'
    returned = re.findall(r'return new (\w+)\(\);', '\n'.join(selected))
    wanted = 'Idas3EosTransport' if expected else 'Idas3UnityRelayTransport'
    assert returned == [wanted], (symbols, returned, wanted)
    return wanted


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sdk-source', type=Path, required=True, help='Real package/Runtime/EOS_SDK or staged Assets/EOS_SDK directory')
    parser.add_argument('--sdk-archive', type=Path, help='Optional original .tgz: verifies repository pin and every managed source byte')
    parser.add_argument('--dotnet', default='dotnet', help='Path to an installed .NET 8 dotnet host')
    parser.add_argument('--report-dir', type=Path, help='Keep logs, assemblies and report here (otherwise use a temporary directory)')
    args = parser.parse_args()
    sdk_root = args.sdk_source.resolve(strict=True)
    sdk_sources = sorted(sdk_root.rglob('*.cs'))
    assert (sdk_root / 'Core/Common.cs').is_file() and (sdk_root / 'Generated/Bindings.cs').is_file(), 'Not a complete EOS SDK source tree'
    assert len(sdk_sources) > 1000, 'Incomplete EOS managed source tree'
    source_hashes = {str(path.relative_to(sdk_root)).replace(os.sep, '/'): digest(path) for path in sdk_sources}
    provenance = 'Local source tree only; archive provenance not verified by this run'
    if args.sdk_archive:
        pin_ast = ast.parse((ROOT / 'Tools/prepare-eos-android.py').read_text(encoding='utf-8'))
        pinned = next(ast.literal_eval(node.value) for node in pin_ast.body if isinstance(node, ast.Assign)
                      and any(isinstance(target, ast.Name) and target.id == 'SHA256' for target in node.targets))
        assert digest(args.sdk_archive) == pinned, 'EOS archive differs from the repository SHA-256 pin'
        prefix = 'package/Runtime/EOS_SDK/'
        with tarfile.open(args.sdk_archive) as bundle:
            archived = {member.name[len(prefix):]: hashlib.sha256(bundle.extractfile(member).read()).hexdigest()
                        for member in bundle.getmembers() if member.isfile() and member.name.startswith(prefix) and member.name.endswith('.cs')}
        assert source_hashes == archived, 'Managed source tree differs from the pinned SDK archive'
        provenance = 'Every managed source matches pinned archive SHA-256 ' + pinned
    dotnet = shutil.which(args.dotnet)
    if not dotnet:
        raise RuntimeError('Install .NET 8 SDK or pass --dotnet /path/to/dotnet')
    listing = subprocess.check_output([dotnet, '--list-sdks'], text=True)
    installed = [match for line in listing.splitlines() if (match := re.fullmatch(r'(8\.\S+) \[(.+)\]', line))]
    if not installed:
        raise RuntimeError('A .NET 8 SDK with netstandard2.1 and net8.0 reference packs is required')
    match = installed[-1]
    sdk_home = Path(match.group(2)) / match.group(1)
    dotnet_home = sdk_home.parent.parent
    compiler = sdk_home / 'Roslyn/bincore/csc.dll'
    standard_refs = sorted((dotnet_home / 'packs/NETStandard.Library.Ref/2.1.0/ref/netstandard2.1').glob('*.dll'))
    net8_packs = sorted((dotnet_home / 'packs/Microsoft.NETCore.App.Ref').glob('8.*/ref/net8.0'))
    assert standard_refs and net8_packs, 'Missing offline framework reference packs'
    host_refs = sorted(net8_packs[-1].glob('*.dll'))
    temp = tempfile.TemporaryDirectory(prefix='idas3-eos-compile-') if not args.report_dir else None
    proof = args.report_dir.resolve() if args.report_dir else Path(temp.name)
    proof.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
                       DOTNET_NOLOGO='1', DOTNET_CLI_HOME=str(proof / 'dotnet-home'))

    def execute(command, log):
        completed = subprocess.run(command, cwd=proof, env=environment, capture_output=True, text=True)
        output = completed.stdout + completed.stderr
        (proof / log).write_text(output, encoding='utf-8')
        if completed.returncode:
            raise RuntimeError(f'{log} failed:\n{output}')
        return output

    def compile_cs(name, output, sources, refs, symbols=(), executable=False, vendor=False):
        options = ['/nologo', '/nostdlib+', '/deterministic+', '/langversion:9.0', '/nullable:disable',
                   '/target:' + ('exe' if executable else 'library'), '/out:' + str(output)]
        if not vendor:
            # Unity JsonUtility populates Config fields; unassigned-field warnings are expected.
            options += ['/warnaserror+', '/nowarn:0649']
        if symbols:
            options += ['/define:' + ';'.join(symbols)]
        options += ['/reference:' + str(path) for path in refs]
        options += [str(path) for path in sources]
        response = proof / (name + '.rsp')
        response.write_text('\n'.join('"' + option + '"' for option in options) + '\n', encoding='utf-8')
        execute([dotnet, str(compiler), '/noconfig', '@' + str(response)], name + '-compile.log')

    report = {'passed': False, 'scope': 'Managed source compilation against real EOS SDK and synthetic Unity configuration/metadata probes; no Unity, IL2CPP, Xcode, native link, device, authentication, lobby or relay validation.',
              'sdk_provenance': provenance, 'sdk_source_count': len(sdk_sources), 'sdk_source_sha256': source_hashes,
              'dotnet_sdk': match.group(1), 'production_source_sha256': {str(path.relative_to(ROOT)): digest(path) for path in SOURCES + [ROOT / 'Assets/Scripts/Multiplayer/Idas3MultiplayerSession.cs']},
              'test_source_sha256': {path.name: digest(path) for path in (Path(__file__), TESTS / 'EosCompileProbe.cs', TESTS / 'EosCompileUnityFakes.cs')}, 'cases': []}
    # Invalidate any prior success before compiling into a reused report directory.
    (proof / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    probe = proof / 'EosCompileProbe.dll'
    compile_cs('probe', probe, [TESTS / 'EosCompileProbe.cs'], host_refs, executable=True)
    (proof / 'EosCompileProbe.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {
        'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': '8.0.0'}}}), encoding='utf-8')
    for name, symbols, present, library, platform in CASES:
        folder = proof / name
        folder.mkdir(exist_ok=True)
        sdk = folder / 'Epic.OnlineServices.dll'
        if present:
            compile_cs(name + '-sdk', sdk, sdk_sources, standard_refs, symbols, vendor=True)
        game = folder / 'Idas3.Eos.CompileSmoke.dll'
        refs = standard_refs + ([sdk] if present else [])
        compile_cs(name + '-game', game, SOURCES + [TESTS / 'EosCompileUnityFakes.cs'], refs, symbols)
        selected = selector_check(symbols, present)
        output = execute([dotnet, str(probe), str(game), 'present' if present else 'absent', str(sdk), library,
                          'editor' if 'UNITY_EDITOR' in symbols else 'player', platform], name + '-probe.log')
        checks = [line.split('\t', 1)[1] for line in output.splitlines() if line.startswith('PASS\t')]
        report['cases'].append({'name': name, 'defines': symbols, 'passed': True, 'checks': checks,
                                'factory_source_check': selected, 'sdk_assembly_sha256': digest(sdk) if present else None,
                                'game_assembly_sha256': digest(game)})
        (proof / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print(f'PASS {name}: real-source compile, {len(checks)} safe probe checks, factory selects {selected}', flush=True)
    report['passed'] = True
    report['total_probe_checks'] = sum(len(case['checks']) for case in report['cases'])
    report['total_factory_source_checks'] = len(CASES)
    (proof / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS {len(CASES)} compile configurations; {report['total_probe_checks']} probe checks; {len(CASES)} factory source checks.")
    if args.report_dir:
        print('Report:', proof / 'report.json')
    if temp:
        temp.cleanup()


if __name__ == '__main__':
    main()
