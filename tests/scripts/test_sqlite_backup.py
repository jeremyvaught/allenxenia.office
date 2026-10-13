import hashlib
from contextlib import closing
import importlib.util
import json
import os
from pathlib import Path
import sqlite3
import stat
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location("office_backup", Path(__file__).resolve().parents[2] / "scripts/backup-sqlite.py")
backup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backup)


class SQLiteBackupTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.source = self.root / "source.db"
        self.destination = self.root / "snapshots"
        self.original = sqlite3.connect(str(self.source))
        self.original.execute("PRAGMA journal_mode=WAL")
        self.original.executescript('''
          CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY);
          INSERT INTO __EFMigrationsHistory VALUES ('test_migration');
          CREATE TABLE asp_net_users (id TEXT PRIMARY KEY);
          CREATE TABLE memberships (id TEXT PRIMARY KEY, user_id TEXT REFERENCES asp_net_users(id));
          INSERT INTO asp_net_users VALUES ('private-test-user');
          INSERT INTO memberships VALUES ('original-membership', 'private-test-user');
        ''')
        self.original.commit()

    def tearDown(self):
        self.original.close()
        self.directory.cleanup()

    def test_live_wal_data_is_in_a_self_contained_verified_snapshot(self):
        self.original.execute("INSERT INTO asp_net_users VALUES ('committed-in-live-wal')")
        self.original.commit()
        self.assertGreater(Path(str(self.source) + "-wal").stat().st_size, 0)
        first, manifest = backup.snapshot(self.source, self.destination)
        second, _ = backup.snapshot(self.source, self.destination)
        self.assertNotEqual(first, second)
        self.assertEqual(manifest, json.loads((first / "manifest.json").read_text()))
        self.assertEqual(manifest["sha256"], hashlib.sha256((first / "office.db").read_bytes()).hexdigest())
        self.assertEqual(2, manifest["counts"]["asp_net_users"])
        with closing(sqlite3.connect((first / "office.db").as_uri() + "?mode=ro", uri=True)) as restored:
            self.assertEqual("delete", restored.execute("PRAGMA journal_mode").fetchone()[0])
            self.assertEqual(2, restored.execute("SELECT COUNT(*) FROM asp_net_users").fetchone()[0])
            self.assertEqual([("ok",)], restored.execute("PRAGMA integrity_check").fetchall())
            self.assertEqual([], restored.execute("PRAGMA foreign_key_check").fetchall())
        self.assertEqual({"office.db", "manifest.json"}, {file.name for file in first.iterdir()})
        if os.name != "nt":
            self.assertEqual(0o700, stat.S_IMODE(first.stat().st_mode))
            self.assertTrue(all(stat.S_IMODE(file.stat().st_mode) == 0o600 for file in first.iterdir()))

    def test_corrupt_foreign_keys_publish_no_snapshot_and_leave_source_intact(self):
        self.original.execute("INSERT INTO memberships VALUES ('broken', 'missing-user')")
        self.original.commit()
        with self.assertRaises(ValueError):
            backup.snapshot(self.source, self.destination)
        self.assertEqual([], list(self.destination.iterdir()))
        self.assertEqual(2, self.original.execute("SELECT COUNT(*) FROM memberships").fetchone()[0])

    def test_timeout_cleans_only_its_unpublished_copy(self):
        with mock.patch.object(backup.time, "monotonic", side_effect=[0] + [2] * 100):
            with self.assertRaises(TimeoutError):
                backup.snapshot(self.source, self.destination, timeout_seconds=1)
        self.assertEqual([], list(self.destination.iterdir()))
        self.assertEqual(1, self.original.execute("SELECT COUNT(*) FROM asp_net_users").fetchone()[0])

    def test_missing_source_and_non_private_destination_are_rejected(self):
        with self.assertRaises(FileNotFoundError):
            backup.snapshot(self.root / "missing.db", self.destination)
        self.assertFalse((self.root / "missing.db").exists())
        if os.name != "nt":
            self.destination.mkdir(mode=0o755)
            self.destination.chmod(0o755)
            with self.assertRaises(ValueError):
                backup.snapshot(self.source, self.destination)
            self.assertEqual([], list(self.destination.iterdir()))


if __name__ == "__main__":
    unittest.main()
