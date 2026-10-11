"""Offline staging/packaging regression tests; no SDK login or Unity required."""
import hashlib
import importlib.util
import json
from pathlib import Path
import plistlib
import struct
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


stage = load('eos_ios_stage', ROOT / 'Tools/prepare-eos-ios.py')
verify = load('verify_ios', ROOT / 'Tools/Verify iOS App.py')


def binary(platform=2, cpu=0x0100000C, minimum=15 << 16, install=None):
    name = (install or '@executable_path/Frameworks/EOSSDK.framework/EOSSDK').encode() + b'\0'
    name += b'\0' * ((-len(name)) % 8)
    commands = struct.pack('<6I', 0x32, 24, platform, minimum, 18 << 16, 0)
    commands += struct.pack('<6I', 0xD, 24 + len(name), 24, 0, 0, 0) + name
    return struct.pack('<8I', 0xFEEDFACF, cpu, 0, 6, 2, len(commands), 0, 0) + commands


class EosIOSBuildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.framework = self.root / 'package' / stage.FRAMEWORK
        self.framework.mkdir(parents=True)
        self.write_framework()

    def write_framework(self, data=None, platforms=None):
        (self.framework / 'EOSSDK').write_bytes(data or binary())
        (self.framework / 'Info.plist').write_bytes(plistlib.dumps({
            'CFBundleExecutable': 'EOSSDK', 'CFBundleSupportedPlatforms': platforms or ['iPhoneOS']}))

    def inspect(self):
        fixture_hash = hashlib.sha256((self.framework / 'EOSSDK').read_bytes()).hexdigest()
        with patch.object(stage, 'BINARY_SHA256', fixture_hash):
            return stage.inspect_framework(self.framework)

    def test_accepts_device_structure(self):
        self.assertEqual(self.inspect()['sdk'], 'iphoneos')

    def test_rejects_simulator_binary(self):
        self.write_framework(binary(platform=7))
        with self.assertRaisesRegex(ValueError, 'not Simulator'):
            self.inspect()

    def test_rejects_simulator_plist(self):
        self.write_framework(platforms=['iPhoneSimulator'])
        with self.assertRaisesRegex(ValueError, 'device distribution'):
            self.inspect()

    def test_rejects_wrong_architecture(self):
        self.write_framework(binary(cpu=0x01000007))
        with self.assertRaisesRegex(ValueError, 'ARM64'):
            self.inspect()

    def test_rejects_new_minimum(self):
        self.write_framework(binary(minimum=17 << 16))
        with self.assertRaisesRegex(ValueError, 'minimum iOS'):
            self.inspect()

    def test_rejects_wrong_install_name(self):
        self.write_framework(binary(install='/tmp/EOSSDK'))
        with self.assertRaisesRegex(ValueError, 'install name'):
            self.inspect()

    def test_rejects_unpinned_binary(self):
        with self.assertRaisesRegex(ValueError, 'SHA-256 mismatch'):
            stage.inspect_framework(self.framework)

    def test_rejects_truncated_header(self):
        self.write_framework(b'\xcf\xfa\xed\xfe')
        with self.assertRaisesRegex(ValueError, 'header'):
            self.inspect()

    def test_rejects_truncated_commands(self):
        self.write_framework(binary()[:-8])
        with self.assertRaisesRegex(ValueError, 'load commands'):
            self.inspect()

    def test_rejects_invalid_command_length(self):
        data = bytearray(binary())
        struct.pack_into('<I', data, 36, 0)
        self.write_framework(data)
        with self.assertRaisesRegex(ValueError, 'command length'):
            self.inspect()

    def test_rejects_unpinned_archive_before_extraction(self):
        archive = self.root / 'bad.tgz'
        archive.write_bytes(b'not a real SDK')
        with self.assertRaisesRegex(ValueError, 'archive SHA-256'):
            stage.extract_and_stage(archive, self.root / 'target')
        self.assertFalse((self.root / 'target').exists())

    def test_staging_is_opt_in_and_keeps_config_and_android_untouched(self):
        package = self.root / 'package'
        managed = package / 'Runtime/EOS_SDK/Generated/Platform/InitializeOptions.cs'
        managed.parent.mkdir(parents=True)
        managed.write_text('// synthetic managed fixture')
        project = self.root / 'project'
        android = project / 'Assets/Plugins/Android/keep.txt'
        android.parent.mkdir(parents=True)
        android.write_text('existing Android payload')
        config = project / 'Assets/Resources/Idas3EOS.json'
        config.parent.mkdir(parents=True)
        config.write_text('fixture: unchanged, no credentials')
        rsp = project / 'Assets/csc.rsp'
        rsp.write_text('-define:IDAS3_EOS\n')
        with patch.object(stage, 'BINARY_SHA256', hashlib.sha256(binary()).hexdigest()):
            result = stage.stage(package, project)
        self.assertEqual(result['sdk'], 'iphoneos')
        self.assertEqual(config.read_text(), 'fixture: unchanged, no credentials')
        self.assertEqual(android.read_text(), 'existing Android payload')
        self.assertEqual(rsp.read_text(), '-define:IDAS3_EOS\n')
        meta = (project / 'Assets/Plugins/iOS/EOSSDK.framework.meta').read_text()
        self.assertIn('SDK: DeviceSDK', meta)
        self.assertNotIn('enabled: 1', meta)

    def make_app(self, present=True, marker=True):
        app = self.root / 'game.app'
        app.mkdir(exist_ok=True)
        if present:
            import shutil
            shutil.copytree(self.framework, app / 'Frameworks/EOSSDK.framework')
        if marker:
            path = app / 'Data/Raw/ios-eos-build-id.json'
            path.parent.mkdir(parents=True)
            path.write_text(json.dumps({'schema': 1, 'sdk': 'iphoneos', 'architectures': ['arm64'],
                'eosVersion': '1.19.2.1', 'binarySha256': stage.BINARY_SHA256}))
        notices = app / 'Data/Raw/EOS-Notices'
        notices.mkdir(parents=True, exist_ok=True)
        for name in ('license.txt', 'ThirdPartySoftwareNotice.txt'):
            (notices / name).write_text('synthetic test notice')
        return app

    def test_packaged_device_requires_marker_and_framework(self):
        errors = []
        result = verify.verify_eos(self.make_app(), 'iphoneos', True, errors)
        self.assertEqual(errors, [])
        self.assertTrue(result['present'])

    def test_packaged_device_missing_framework_fails(self):
        errors = []
        verify.verify_eos(self.make_app(present=False), 'iphoneos', True, errors)
        self.assertTrue(any('exactly one' in error for error in errors))

    def test_packaged_device_missing_marker_fails(self):
        errors = []
        verify.verify_eos(self.make_app(marker=False), 'iphoneos', True, errors)
        self.assertIn('Missing EOS build manifest', errors)

    def test_packaged_simulator_with_eos_fails(self):
        errors = []
        verify.verify_eos(self.make_app(), 'iphonesimulator', True, errors)
        self.assertTrue(any('unsupported' in error for error in errors))

    def test_ordinary_build_rejects_leaked_framework(self):
        errors = []
        verify.verify_eos(self.make_app(marker=False), 'iphonesimulator', False, errors)
        self.assertTrue(any('Unexpected EOS' in error for error in errors))

    def test_ordinary_build_without_eos_passes(self):
        errors = []
        result = verify.verify_eos(self.make_app(False, False), 'iphonesimulator', False, errors)
        self.assertEqual(errors, [])
        self.assertFalse(result['present'])

    def test_packaged_device_missing_notice_fails(self):
        app = self.make_app()
        (app / 'Data/Raw/EOS-Notices/license.txt').unlink()
        errors = []
        verify.verify_eos(app, 'iphoneos', True, errors)
        self.assertIn('Missing EOS notice: license.txt', errors)

    def test_duplicate_embedded_framework_fails(self):
        app = self.make_app()
        (app / 'Frameworks/UnityFramework.framework/Frameworks/EOSSDK.framework').mkdir(parents=True)
        errors = []
        verify.verify_eos(app, 'iphoneos', True, errors)
        self.assertTrue(any('exactly one' in error for error in errors))


if __name__ == '__main__':
    unittest.main(verbosity=2)
