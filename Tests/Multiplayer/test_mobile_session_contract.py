"""Compile full production sessions and exchange Android/iOS Hello packets offline.

First run test_eos_mobile_compile.py with a retained --report-dir. This runner
verifies and reuses only that run's real EOS SDK assemblies. See the README.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

from test_eos_mobile_compile import CASES, ROOT, TESTS, digest

PRODUCTION = [ROOT / 'Assets/Scripts/Multiplayer' / name for name in (
    'Idas3MultiplayerTypes.cs', 'Idas3MultiplayerSession.cs', 'Idas3BuildCompatibility.cs',
    'Idas3MultiplayerNative.cs', 'Idas3MultiplayerRecords.cs', 'Idas3MultiplayerRecordChecks.cs',
    'Idas3NetworkImpairment.cs', 'Idas3QuickMatch.cs', 'Idas3EosRuntime.cs', 'Idas3EosTransport.cs')]
PRODUCTION += [ROOT / 'Assets/Scripts' / name for name in ('Idas3PlatformPaths.cs', 'Idas3CourseCatalog.cs')]
TEST_SOURCES = [TESTS / name for name in ('SessionContractFakes.cs', 'SessionContractProbe.cs', 'SessionContractHost.cs')]
PACKS = [('ENNA', 'enna'), ('MYOGI_SPECIAL', 'myogi_special'), ('USUI_SPECIAL', 'usui_special'),
         ('MOMIJI', 'momiji'), ('TSUBAKI', 'tsubaki')]


def fixtures(root, platform):
    # Independent fixture computation: do not derive the expected protocol,
    # version, file order or hashes by parsing the production implementation.
    streaming = root / 'streaming'
    streaming.mkdir(parents=True)
    (streaming / 'idas3-app-version.txt').write_text('0.3.95-contract\n', encoding='utf-8')
    if platform == 'Android':
        packs = root / 'persistent/RuntimeData/RuntimeAssets'
    elif platform == 'IPhonePlayer':
        packs = streaming / 'IDAS3/data/RuntimeAssets'
    else:
        packs = streaming
    identity = 'idas3-mp10-cross-platform-v2-0.3.95-contract'
    for course, (pack, slug) in enumerate(PACKS, 11):
        folder = packs / pack
        folder.mkdir(parents=True)
        (folder / 'menu.idastex').write_bytes(b'fixture-presence-marker')
        names = ['course.id', f'{slug}_path.bin', f'{slug}_path_l.bin', f'{slug}_path_r.bin']
        names += ['race.bin', f'{slug}.rcl'] if course == 15 else ['race-markers.bin', 'collision-0.rcl', 'collision-1.rcl']
        if 12 <= course <= 14:
            names.append('timer-scale.bin')
        parts = []
        for name in names:
            content = f'synthetic simulation input {course}/{name}\n'.encode()
            (folder / name).write_bytes(content)
            parts.append(hashlib.sha256(content).digest())
        identity += f'-{course}-' + base64.b64encode(hashlib.sha256(b''.join(parts)).digest()).decode()
    identity += '-authority1'
    key = 'idas3-build1-' + base64.b64encode(hashlib.sha256(identity.encode()).digest()).decode()
    (root / 'expected-identity.txt').write_text(identity, encoding='utf-8')
    (root / 'expected-key.txt').write_text(key, encoding='utf-8')
    return identity, key


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--eos-compile-report', type=Path, required=True, help='Passing retained directory from test_eos_mobile_compile.py')
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--report-dir', type=Path)
    args = parser.parse_args()
    temp = tempfile.TemporaryDirectory(prefix='idas3-session-contract-') if not args.report_dir else None
    proof = args.report_dir.resolve() if args.report_dir else Path(temp.name)
    proof.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
                       DOTNET_NOLOGO='1', DOTNET_CLI_HOME=str(proof / 'dotnet-home'))
    report = {'passed': False, 'scope': 'Compiled managed session contracts with synthetic files and in-memory peer wire. No Unity, native EOS/game execution, network, authentication, real lobby/relay, IL2CPP, Xcode, or device validation.',
              'production_source_sha256': {str(path.relative_to(ROOT)): digest(path) for path in PRODUCTION},
              'test_source_sha256': {path.name: digest(path) for path in TEST_SOURCES + [Path(__file__), TESTS / 'test_eos_mobile_compile.py']},
              'expected_wire_protocol': 11, 'cases': [], 'cross_platform': []}
    def save():
        (proof / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    save()  # Invalidate old success before tool discovery or compilation.

    def run_command(command, name, timeout=120):
        try:
            result = subprocess.run(command, cwd=proof, env=environment, text=True, capture_output=True, timeout=timeout)
        except subprocess.TimeoutExpired as error:
            def decoded(value):
                return value.decode('utf-8', errors='replace') if isinstance(value, bytes) else value or ''
            output = decoded(error.stdout) + decoded(error.stderr) + f'\nTimed out after {timeout} seconds.\n'
            (proof / (name + '.log')).write_text(output, encoding='utf-8')
            report['failure'] = {'command': name, 'reason': 'timeout', 'timeout_seconds': timeout}
            save()
            raise RuntimeError(f'{name} timed out:\n{output}') from error
        output = result.stdout + result.stderr
        (proof / (name + '.log')).write_text(output, encoding='utf-8')
        if result.returncode:
            report['failure'] = {'command': name, 'reason': 'nonzero exit', 'exit_code': result.returncode}
            save()
            raise RuntimeError(f'{name} failed:\n{output}')
        return output

    def execute(command, name):
        return [line.split('\t', 1)[1] for line in run_command(command, name).splitlines() if line.startswith('PASS\t')]

    previous_root = args.eos_compile_report.resolve(strict=True)
    previous_file = previous_root / 'report.json'
    previous = json.loads(previous_file.read_text(encoding='utf-8'))
    assert previous['passed'], 'The real-SDK compile smoke must have passed'
    prior_cases = {case['name']: case for case in previous['cases']}
    for name, symbols, eos, _, _ in CASES:
        assert prior_cases[name]['passed'] and prior_cases[name]['defines'] == symbols, 'SDK symbol matrix differs'
        if eos:
            sdk = previous_root / name / 'Epic.OnlineServices.dll'
            assert digest(sdk) == prior_cases[name]['sdk_assembly_sha256'], 'SDK assembly no longer matches compile report'
    dotnet = shutil.which(args.dotnet)
    if not dotnet:
        raise RuntimeError('Install .NET 8 SDK or pass --dotnet /path/to/dotnet')
    sdks = run_command([dotnet, '--list-sdks'], 'dotnet-list-sdks', timeout=30)
    installed = [match for line in sdks.splitlines() if (match := re.fullmatch(r'(8\.\S+) \[(.+)\]', line))]
    assert installed, '.NET 8 SDK is required'
    selected = installed[-1]
    sdk_home = Path(selected.group(2)) / selected.group(1)
    home = sdk_home.parent.parent
    refs = sorted((home / 'packs/NETStandard.Library.Ref/2.1.0/ref/netstandard2.1').glob('*.dll'))
    host_packs = sorted((home / 'packs/Microsoft.NETCore.App.Ref').glob('8.*/ref/net8.0'))
    assert refs and host_packs, 'Offline netstandard2.1 and net8.0 reference packs are required'
    report.update({'eos_compile_report_sha256': digest(previous_file), 'sdk_provenance': previous['sdk_provenance'],
                   'dotnet_sdk': selected.group(1)})
    save()

    def compile_cs(name, output, sources, references, symbols=(), executable=False):
        options = ['/nologo', '/nostdlib+', '/deterministic+', '/langversion:9.0', '/nullable:disable', '/warnaserror+', '/nowarn:0649',
                   '/target:' + ('exe' if executable else 'library'), '/out:' + str(output)]
        if symbols:
            options += ['/define:' + ';'.join(symbols)]
        options += ['/reference:' + str(path) for path in references]
        options += [str(path) for path in sources]
        response = proof / (name + '.rsp')
        response.write_text('\n'.join('"' + item + '"' for item in options) + '\n', encoding='utf-8')
        execute([dotnet, str(sdk_home / 'Roslyn/bincore/csc.dll'), '/noconfig', '@' + str(response)], name + '-compile')

    host = proof / 'SessionContractHost.dll'
    compile_cs('host', host, [TESTS / 'SessionContractHost.cs'], sorted(host_packs[-1].glob('*.dll')), executable=True)
    (proof / 'SessionContractHost.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {
        'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': '8.0.0'}}}), encoding='utf-8')
    base = Path(tempfile.mkdtemp(prefix='fixtures-', dir=proof))
    commands = {}
    for name, symbols, eos, _, sdk_platform in CASES:
        folder = proof / name
        folder.mkdir(exist_ok=True)
        # Unity target symbols remain Android/iOS in Editor, but the running
        # Editor reports its desktop platform, never the selected build target.
        platform = 'LinuxEditor' if 'UNITY_EDITOR' in symbols else sdk_platform or ('IPhonePlayer' if 'UNITY_IOS' in symbols else 'Android' if 'UNITY_ANDROID' in symbols else 'WindowsPlayer')
        fixture = base / name
        fixture.mkdir()
        identity, key = fixtures(fixture, platform)
        references = list(refs)
        if eos:
            sdk = folder / 'Epic.OnlineServices.dll'
            shutil.copyfile(previous_root / name / sdk.name, sdk)
            references.append(sdk)
        game = folder / 'Idas3.Session.Contract.dll'
        compile_cs(name, game, PRODUCTION + TEST_SOURCES[:2], references, symbols)
        factory = 'Idas3EosTransport' if eos else 'Idas3UnityRelayTransport'
        command = [dotnet, str(host), str(game), str(fixture), platform, 'editor' if 'UNITY_EDITOR' in symbols else 'player', factory]
        commands[name] = (command, fixture)
        checks = execute(command, name + '-probe')
        report['cases'].append({'name': name, 'defines': symbols, 'runtime_platform': platform,
                                'is_editor': 'UNITY_EDITOR' in symbols, 'passed': True, 'checks': checks,
                                'identity': identity, 'matchmaking_key': key, 'game_assembly_sha256': digest(game),
                                'sdk_assembly_sha256': digest(sdk) if eos else None, 'fixture_dir': str(fixture)})
        save()
        print(f'PASS {name}: full session compile, {len(checks)} contract checks', flush=True)
    for receiver, sender in [('android', 'ios'), ('ios', 'android'), ('android', 'ios_both_flags'), ('ios_both_flags', 'android')]:
        command, _ = commands[receiver]
        _, peer = commands[sender]
        name = sender + '-to-' + receiver
        checks = execute(command + [str(peer / 'host-hello.bin'), str(peer / 'guest-hello.bin')], name)
        report['cross_platform'].append({'sender': sender, 'receiver': receiver, 'passed': True, 'checks': checks})
        save()
        print(f'PASS {name}: {len(checks)} cross-platform admission/rejection checks', flush=True)
    assert report['production_source_sha256'] == {str(path.relative_to(ROOT)): digest(path) for path in PRODUCTION}, 'Production sources changed during verification; rerun against a stable snapshot'
    report['passed'] = True
    report['total_checks'] = sum(len(item['checks']) for item in report['cases'] + report['cross_platform'])
    save()
    print(f"PASS {len(CASES)} compiled session configurations; {report['total_checks']} contract checks.")
    if args.report_dir:
        print('Report:', proof / 'report.json')
    if temp:
        temp.cleanup()


if __name__ == '__main__':
    main()
