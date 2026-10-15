"""Packaging regressions: do not ship a device framework in Simulator builds."""
import importlib.util
import json
from pathlib import Path
import plistlib
import struct
import tempfile
import unittest

TOOLS = Path(__file__).resolve().parents[1]


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, TOOLS / filename)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


staging = module('eos_staging', 'prepare-eos-mobile.py')
verifier = module('ios_verifier', 'Verify iOS App.py')


def macho(platform):
    return struct.pack('<8I', 0xfeedfacf, 0x0100000c, 0, 6, 1, 24, 0, 0) + struct.pack('<6I', 0x32, 24, platform, 0, 0, 0)


class EosMobilePackaging(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.project = self.root / 'project'
        self.package = self.root / 'package'
        managed = self.package / 'Runtime/EOS_SDK'
        managed.mkdir(parents=True)
        (managed / 'Binding.cs').write_text('// fixture')
        self.framework = self.package / 'Runtime/iOS/EOSSDK.framework'
        self.framework.mkdir(parents=True)
        (self.framework / 'EOSSDK').write_bytes(macho(2))
        (self.framework / 'Info.plist').write_bytes(plistlib.dumps({'CFBundleExecutable': 'EOSSDK'}))
        self.framework.with_suffix('.framework.meta').write_text('PluginImporter: {}')

    def test_device_framework_then_simulator_removes_native_sdk_and_credentials(self):
        staging.stage(self.package, self.project, 'iphoneos')
        native = self.project / 'Assets/Plugins/iOS/EOSSDK.framework/EOSSDK'
        self.assertEqual(native.read_bytes(), macho(2))
        config = self.project / 'Assets/Resources/Idas3EOS.json'
        config.parent.mkdir()
        config.write_text('{}')
        staging.stage(None, self.project, 'iphonesimulator')
        self.assertFalse(native.exists())
        self.assertFalse(config.exists())
        self.assertFalse((self.project / 'Assets/EOS_SDK').exists())
        defines = (self.project / 'Assets/csc.rsp').read_text().splitlines()
        self.assertNotIn('-define:IDAS3_EOS', defines)
        self.assertIn('-define:IDAS3_EOS_IOS_SIMULATOR', defines)
        staging.stage(self.package, self.project, 'iphoneos')
        self.assertTrue(native.is_file())
        self.assertNotIn('-define:IDAS3_EOS_IOS_SIMULATOR', (self.project / 'Assets/csc.rsp').read_text().splitlines())

    def test_existing_compiler_options_are_preserved_without_duplicate_defines(self):
        rsp = self.project / 'Assets/csc.rsp'
        rsp.parent.mkdir(parents=True)
        rsp.write_text('-unsafe\n-define:OTHER_SETTING\n')
        for platform in ('android', 'iphoneos', 'iphonesimulator', 'iphoneos'):
            staging.configure_mode(self.project, platform)
        self.assertEqual(rsp.read_text().splitlines(), ['-unsafe', '-define:OTHER_SETTING', '-define:IDAS3_EOS'])

    def test_wrong_sdk_and_truncated_frameworks_are_rejected(self):
        for payload in (macho(7), macho(1), macho(2)[:40]):
            (self.framework / 'EOSSDK').write_bytes(payload)
            with self.assertRaises(ValueError):
                staging.stage(self.package, self.project, 'iphoneos')

    def test_missing_embedded_framework_fails_required_device_verification(self):
        raw = self.project / 'Data/Raw'
        raw.mkdir(parents=True)
        (raw / 'idas3-eos-build.json').write_text(json.dumps({'schema': 'idas3-eos-build-v1',
            'platform': 'iphoneos', 'transport': 'eos', 'sdkVersion': staging.SDK_VERSION}))
        _, errors = verifier.verify_eos(self.project, 'iphoneos', True)
        self.assertIn('EOS framework was not embedded in the app', errors)

    def test_simulator_requires_no_sdk(self):
        staging.stage(None, self.project, 'iphonesimulator')
        self.assertEqual(json.loads((self.project / 'Assets/StreamingAssets/idas3-eos-build.json').read_text())['transport'], 'unavailable')


if __name__ == '__main__':
    unittest.main()
