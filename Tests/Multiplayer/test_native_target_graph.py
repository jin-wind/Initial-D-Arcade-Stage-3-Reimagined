"""Configure-only regression tests for native platform target selection.

The real CMake source is evaluated by the installed HOST compiler. Synthetic
platform branches test target-graph contracts, NOT Android/iOS toolchains,
compilation, ABI compatibility, or linking. No SDK, network or credentials.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'Native'
CASES = ('linux_host', 'android_branch', 'ios_branch', 'macos_branch', 'windows_branch', 'cross_host')


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def evaluate(cmake, proof, case):
    build = proof / case
    build.mkdir()
    query = build / '.cmake/api/v1/query'
    query.mkdir(parents=True)
    (query / 'codemodel-v2').touch()
    # Post-project injection deliberately retains the host toolchain. In
    # particular an "ios_branch" result must never be called an iOS build.
    hook = build / 'platform-branch.cmake'
    definitions = ['set(WIN32 FALSE)', 'set(APPLE FALSE)', 'set(ANDROID FALSE)', 'set(CMAKE_CROSSCOMPILING FALSE)']
    if case == 'android_branch':
        definitions += ['set(ANDROID TRUE)']
    elif case in ('ios_branch', 'macos_branch'):
        definitions += ['set(APPLE TRUE)']
        if case == 'ios_branch':
            definitions += ['set(CMAKE_SYSTEM_NAME iOS)', 'set(CMAKE_CROSSCOMPILING TRUE)']
    elif case == 'windows_branch':
        definitions += ['set(WIN32 TRUE)']
    elif case == 'cross_host':
        definitions += ['set(CMAKE_CROSSCOMPILING TRUE)']
    hook.write_text('\n'.join(definitions) + '\n', encoding='utf-8')
    command = [cmake, '-S', str(SOURCE), '-B', str(build), '-DCMAKE_BUILD_TYPE=Release',
               '-DCMAKE_PROJECT_InitialDRemake_INCLUDE=' + str(hook),
               '-DCMAKE_RUNTIME_OUTPUT_DIRECTORY=' + str(build / 'bin'),
               '-DIDAS3_ANDROID_PLUGIN_OUTPUT_DIRECTORY=' + str(build / 'android'),
               '-DIDAS3_MACOS_PLUGIN_OUTPUT_DIRECTORY=' + str(build / 'macos'),
               '-DIDAS3_IOS_PLUGIN_OUTPUT_DIRECTORY=' + str(build / 'ios')]
    completed = subprocess.run(command, capture_output=True, text=True, timeout=90)
    (build / 'configure.log').write_text(completed.stdout + completed.stderr, encoding='utf-8')
    check(completed.returncode == 0, f'{case}: configure failed; see {build / "configure.log"}')
    reply = build / '.cmake/api/v1/reply'
    index = json.loads(next(reply.glob('index-*.json')).read_text())
    model = json.loads((reply / index['reply']['codemodel-v2']['jsonFile']).read_text())
    contracts = []
    for configuration in model['configurations']:
        targets = {entry['name']: json.loads((reply / entry['jsonFile']).read_text()) for entry in configuration['targets']}
        portable = case in ('android_branch', 'ios_branch', 'macos_branch')
        expected_plugin = portable or case == 'windows_branch'
        check(('Idas3Unity' in targets) == expected_plugin, f'{case}: unexpected/missing Idas3Unity')
        expected_headless = case in ('linux_host', 'windows_branch')
        for name in ('headless_online_peer', 'headless_multiplayer_checks'):
            check((name in targets) == expected_headless, f'{case}: wrong diagnostic target selection: {name}')
        if expected_plugin:
            plugin = targets['Idas3Unity']
            check(plugin['type'] == ('STATIC_LIBRARY' if case == 'ios_branch' else 'SHARED_LIBRARY'), f'{case}: wrong plugin library kind')
            defines = {item['define'] for group in plugin.get('compileGroups', []) for item in group.get('defines', [])}
            check(('IDAS3_PORTABLE_SCENE' in defines) == portable, f'{case}: wrong portable-scene define')
            fragments = ' '.join(item['fragment'] for item in plugin.get('link', {}).get('commandFragments', []))
            if portable:
                check(not any(name in fragments.lower() for name in ('d3d11', 'dxgi', 'd3dcompiler', 'xinput')), f'{case}: Windows library leaked into mobile/Apple plugin')
                for name in ('idas3_original', 'idas3_native_assets', 'idas3_core'):
                    core = targets[name]
                    check(core['type'] == ('OBJECT_LIBRARY' if case in ('ios_branch', 'macos_branch') else 'STATIC_LIBRARY'), f'{case}: wrong source-closure kind for {name}')
                    check(core['id'] in {item['id'] for item in plugin.get('dependencies', [])}, f'{case}: missing plugin dependency on {name}')
                    if case in ('ios_branch', 'macos_branch'):
                        generated = [item['path'] for item in plugin.get('sources', []) if item.get('isGenerated')]
                        for item in core.get('sources', []):
                            if 'compileGroupIndex' in item:
                                suffix = '/CMakeFiles/' + name + '.dir/' + item['path'] + '.o'
                                check(any(path.endswith(suffix) for path in generated), f'{case}: missing compiled object from {name}: {item["path"]}')
                    core_defines = {item['define'] for group in core.get('compileGroups', []) for item in group.get('defines', [])}
                    check('IDAS3_PORTABLE_SCENE' in core_defines, f'{case}: portable define missing from {name}')
                    flags = ' '.join(item['fragment'] for group in core.get('compileGroups', []) for item in group.get('compileCommandFragments', []))
                    check('-ffp-contract=off' in flags and '-fno-fast-math' in flags, f'{case}: strict float flags missing from {name}')
            if case == 'android_branch':
                check('-llog' in fragments and '-landroid' in fragments, f'{case}: Android system dependencies missing')
                check('max-page-size=16384' in fragments, f'{case}: Android 16 KiB alignment option missing')
        contracts.append({'configuration': configuration['name'], 'plugin_present': expected_plugin,
                          'plugin_type': targets['Idas3Unity']['type'] if expected_plugin else None,
                          'headless_present': expected_headless, 'portable': portable, 'passed': True})
    return {'name': case, 'passed': True, 'contracts': contracts}



def mutation_checks(cmake, proof):
    """Prove that the checker catches the port's specific dangerous regressions."""
    global SOURCE
    original = SOURCE
    changes = (
        ('windows_only_plugin', 'android_branch', 'CMakeLists.txt',
         'if(WIN32 OR ANDROID OR APPLE)', 'if(WIN32)', 'missing Idas3Unity'),
        ('ios_incomplete_archive', 'ios_branch', 'CMakeLists.txt',
         'set(IDAS3_CORE_LIBRARY_KIND OBJECT)', 'set(IDAS3_CORE_LIBRARY_KIND STATIC)', 'source-closure kind'),
        ('ios_missing_objects', 'ios_branch', 'unity_plugin.cmake',
         'target_link_libraries(Idas3Unity PRIVATE idas3_core idas3_native_assets idas3_original)',
         '# omitted object-library dependency', 'missing plugin dependency'),
        ('android_headless_leak', 'android_branch', 'CMakeLists.txt',
         'if(NOT ANDROID AND NOT APPLE AND NOT CMAKE_CROSSCOMPILING)',
         'if(NOT APPLE AND NOT CMAKE_CROSSCOMPILING)', 'diagnostic target selection'),
    )
    results = []
    try:
        for name, case, filename, before, after, expected in changes:
            mutation = proof / ('mutation-' + name)
            mutation.mkdir()
            fixture = mutation / 'Native'
            fixture.mkdir()
            for directory in ('src', 'tests', 'tools', 'data'):
                (fixture / directory).symlink_to(original / directory, target_is_directory=True)
            for source in ('CMakeLists.txt', 'unity_plugin.cmake'):
                text = (original / source).read_text(encoding='utf-8')
                if source == filename:
                    check(text.count(before) == 1, 'Mutation no longer applies: ' + name)
                    text = text.replace(before, after)
                (fixture / source).write_text(text, encoding='utf-8')
            SOURCE = fixture
            try:
                evaluate(cmake, mutation, case)
            except AssertionError as error:
                check(expected in str(error), f'{name}: failed for wrong reason: {error}')
                results.append({'name': name, 'detected': True, 'rejection': str(error)})
                print('PASS detects', name, flush=True)
            else:
                raise AssertionError('Checker missed mutation: ' + name)
    finally:
        SOURCE = original
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cmake', default='cmake')
    parser.add_argument('--report-dir', type=Path)
    args = parser.parse_args()
    if not sys.platform.startswith('linux'):
        raise RuntimeError('This synthetic target-graph check is Linux-host-only; use real platform toolchains elsewhere.')
    cmake = shutil.which(args.cmake)
    if not cmake:
        raise RuntimeError('CMake 3.24+ required; pass --cmake /path/to/cmake')
    temporary = tempfile.TemporaryDirectory(prefix='idas3-target-graph-') if not args.report_dir else None
    proof = Path(temporary.name) if temporary else args.report_dir.resolve()
    if not temporary:
        proof.mkdir(parents=True, exist_ok=False)
    report = {'passed': False, 'scope': 'Synthetic platform CMake target-graph checks using host toolchain only; not platform builds or device proof.',
              'source_sha256': {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                                for p in (SOURCE / 'CMakeLists.txt', SOURCE / 'unity_plugin.cmake')}, 'cases': []}
    try:
        for case in CASES:
            report['cases'].append(evaluate(cmake, proof, case))
            print('PASS', case, flush=True)
        report['mutation_checks'] = mutation_checks(cmake, proof)
        report['passed'] = True
    finally:
        (proof / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f'PASS {len(CASES)} host-evaluated platform target graphs; 4 known regressions detected; no mobile compilation.')
    if args.report_dir:
        print('Report:', proof / 'report.json')
    if temporary:
        temporary.cleanup()


if __name__ == '__main__':
    main()
