import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('release_package', Path(__file__).resolve().parents[2] / 'scripts/package-release.py')
release = importlib.util.module_from_spec(spec); spec.loader.exec_module(release)


class ReleasePackageTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(); self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        for name in release.REQUIRED:
            path = self.root / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(b'public build')
        (self.root / 'appsettings.json').write_text(json.dumps({'Database': {'Provider': 'MySql'}, 'Logging': {'LogLevel': {'Default': 'Warning'}}, 'AllowedHosts': 'office.example'}))
        (self.root / 'AllenBuilding.Web.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'tfm': 'net10.0'}}))

    def test_verified_release_detects_tampered_missing_and_added_files(self):
        release.seal(self.root, 'a' * 40); self.assertEqual('a' * 40, release.verify(self.root)['source_revision'])
        path = self.root / 'wwwroot/css/app.css'; original = path.read_bytes(); path.write_bytes(b'changed')
        with self.assertRaises(ValueError): release.verify(self.root)
        path.write_bytes(original); path.unlink()
        with self.assertRaises(ValueError): release.verify(self.root)
        path.write_bytes(original); (self.root / 'extra.dll').write_bytes(b'addition')
        with self.assertRaises(ValueError): release.verify(self.root)

    def test_private_config_database_keys_and_hidden_files_are_rejected(self):
        for name in ['appsettings.Development.json', 'appsettings.Production.json', 'office.db', 'wrap.pfx', '.env', 'wwwroot/private.key']:
            with self.subTest(name=name):
                path = self.root / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(b'private')
                with self.assertRaises(ValueError): release.seal(self.root, 'a' * 40)
                path.unlink()
        settings = self.root / 'appsettings.json'; public = settings.read_text()
        settings.write_text(json.dumps({'Database': {'Provider': 'MySql'}, 'ConnectionStrings': {'Office': 'private'}}))
        with self.assertRaises(ValueError): release.seal(self.root, 'a' * 40)
        settings.write_text(public); self.assertFalse((self.root / 'manifest.json').exists())

    def test_links_and_manifest_traversal_are_rejected_without_following_them(self):
        private = self.root.parent / (self.root.name + '-private'); private.write_bytes(b'private'); self.addCleanup(private.unlink)
        link = self.root / 'linked.dll'; link.symlink_to(private)
        with self.assertRaises(ValueError): release.seal(self.root, 'a' * 40)
        link.unlink(); release.seal(self.root, 'a' * 40)
        manifest = self.root / 'manifest.json'; data = json.loads(manifest.read_text()); data['files']['../outside.dll'] = {'bytes': 7, 'sha256': 'a' * 64}; manifest.write_text(json.dumps(data))
        with self.assertRaises(ValueError): release.verify(self.root)
        self.assertEqual(b'private', private.read_bytes())

    def test_manifest_and_asset_directory_links_are_rejected(self):
        release.seal(self.root, 'a' * 40)
        manifest = self.root / 'manifest.json'; copied = self.root.parent / (self.root.name + '-manifest'); copied.write_bytes(manifest.read_bytes()); self.addCleanup(copied.unlink)
        manifest.unlink(); manifest.symlink_to(copied)
        with self.assertRaises(ValueError): release.verify(self.root)
        manifest.unlink(); manifest.write_bytes(copied.read_bytes())
        (self.root / 'wwwroot/linked').symlink_to(self.root.parent, target_is_directory=True)
        with self.assertRaises(ValueError): release.verify(self.root)

    def test_native_profile_requires_bundled_runtime_and_x64_executable(self):
        with self.assertRaises(ValueError): release.seal(self.root, 'a' * 40, 'linux-x64')
        runtime = {'runtimeOptions': {'tfm': 'net10.0', 'includedFrameworks': [{'name': 'Microsoft.NETCore.App', 'version': '10.0.9'}, {'name': 'Microsoft.AspNetCore.App', 'version': '10.0.9'}]}}
        (self.root / 'AllenBuilding.Web.runtimeconfig.json').write_text(json.dumps(runtime))
        for name in ['libhostfxr.so', 'libhostpolicy.so', 'libcoreclr.so']:
            (self.root / name).write_bytes(b'fixture native library')
        host = self.root / 'AllenBuilding.Web'
        host.write_bytes(b'\x7fELF\x02\x01' + b'\0' * 12 + b'\x3e\x00')
        host.chmod(0o755)
        with self.assertRaises(ValueError): release.inventory(self.root, 'portable')
        release.seal(self.root, 'a' * 40, 'linux-x64')
        self.assertEqual('linux-x64', release.verify(self.root)['profile'])
        host.chmod(0o644)
        with self.assertRaises(ValueError): release.verify(self.root)
        host.chmod(0o755)
        host.write_bytes(b'\x7fELF\x02\x01' + b'\0' * 12 + b'\xb7\x00')
        with self.assertRaises(ValueError): release.inventory(self.root, 'linux-x64')


if __name__ == '__main__':
    unittest.main()
