#!/usr/bin/env python3
"""Verified local SQLite snapshots. Production MySQL and protected keys have separate recovery procedures."""
import argparse
import datetime
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import stat
import time
import uuid


def sync_directory(path):
    if os.name != "nt":
        fd = os.open(path, os.O_RDONLY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)


def snapshot(source_path, backup_root, timeout_seconds=30):
    if not 1 <= timeout_seconds <= 300:
        raise ValueError("Choose a backup timeout between 1 and 300 seconds.")
    source_path = Path(source_path).resolve(strict=True)
    if not source_path.is_file():
        raise ValueError("The source must be an existing SQLite database file.")
    backup_root = Path(backup_root).resolve()
    backup_root.mkdir(mode=0o700, parents=True, exist_ok=True)
    if not backup_root.is_dir():
        raise ValueError("The backup destination must be a directory.")
    if os.name != "nt" and (backup_root.stat().st_uid != os.getuid() or stat.S_IMODE(backup_root.stat().st_mode) != 0o700):
        raise ValueError("Use an owned backup directory with permissions 0700.")
    deadline = time.monotonic() + timeout_seconds
    identifier = uuid.uuid4().hex
    pending = backup_root / (".pending-" + identifier)
    pending.mkdir(mode=0o700)
    published = backup_root / ("snapshot-" + datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + identifier[:8])
    database = pending / "office.db"

    def expired():
        return time.monotonic() >= deadline

    def progress(_status, _remaining, _total):
        if expired():
            raise TimeoutError("Backup exceeded its time budget.")

    def quoted(identifier):
        return '"' + identifier.replace('"', '""') + '"'

    try:
        fd = os.open(database, os.O_CREAT | os.O_EXCL | os.O_RDWR, 0o600)
        os.close(fd)
        # mode=rw refuses a missing source and lets SQLite safely initialize WAL sidecars.
        # query_only prevents source writes. Never ignore a live WAL with immutable=1 or copy its file directly.
        with closing(sqlite3.connect(source_path.as_uri() + "?mode=rw", uri=True, timeout=min(timeout_seconds, 5))) as original:
            original.execute("PRAGMA query_only=ON")
            original.set_progress_handler(lambda: int(expired()), 1000)
            original.execute("BEGIN")
            tables = [row[0] for row in original.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")]
            if not {"__EFMigrationsHistory", "asp_net_users", "memberships"}.issubset(tables):
                raise ValueError("The source is not an Office application database.")
            migration = original.execute("SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1").fetchone()
            if migration is None:
                raise ValueError("The source has no applied application migration.")
            counts = {table: original.execute("SELECT COUNT(*) FROM " + quoted(table)).fetchone()[0] for table in tables}
            with closing(sqlite3.connect(str(database))) as copied:
                original.backup(copied, pages=128, progress=progress, sleep=0.05)
            original.rollback()
        # Reopen after the backup: its copied WAL header must be read afresh before changing journal mode.
        with closing(sqlite3.connect(str(database))) as copied:
            copied.set_progress_handler(lambda: int(expired()), 1000)
            if copied.execute("PRAGMA journal_mode=DELETE").fetchone()[0] != "delete":
                raise ValueError("The snapshot could not be made self-contained.")
            if copied.execute("PRAGMA integrity_check").fetchall() != [("ok",)] or copied.execute("PRAGMA foreign_key_check").fetchone() is not None:
                raise ValueError("The copied database failed integrity or foreign-key verification.")
            if any(copied.execute("SELECT COUNT(*) FROM " + quoted(table)).fetchone()[0] != count for table, count in counts.items()):
                raise ValueError("The copied database does not match the consistent source snapshot.")
        # Some SQLite builds leave an inert shared-memory file after conversion. This private copy has no open connections;
        # its fresh connection verified DELETE mode. Remove only that known sidecar, never a source WAL or journal.
        shared_memory = pending / "office.db-shm"
        if shared_memory.exists():
            shared_memory.unlink()
        with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as readonly:
            if readonly.execute("PRAGMA journal_mode").fetchone()[0] != "delete":
                raise ValueError("The published snapshot would require WAL sidecars.")
        if any(pending.glob("office.db-*")):
            raise ValueError("The copied database still has journal sidecars.")
        digest = hashlib.sha256()
        with database.open("rb") as stream:
            for chunk in iter(lambda: stream.read(131072), b""):
                progress(0, 0, 0)
                digest.update(chunk)
            os.fsync(stream.fileno())
        manifest = {"format": 1, "provider": "Sqlite", "migration": migration[0], "counts": counts,
                    "sha256": digest.hexdigest(), "created_at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                    "scope": "Database only; protected keys and production MySQL recovery are separate."}
        fd = os.open(pending / "manifest.json", os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(manifest, stream, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        progress(0, 0, 0)
        sync_directory(pending)
        pending.rename(published)
        sync_directory(backup_root)
        return published, manifest
    finally:
        # Only remove this invocation's un-published temporary directory; never an existing snapshot or source.
        if pending.exists():
            shutil.rmtree(pending)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source")
    parser.add_argument("backup_directory")
    parser.add_argument("--timeout-seconds", type=int, default=30)
    args = parser.parse_args()
    try:
        folder, manifest = snapshot(args.source, args.backup_directory, args.timeout_seconds)
        print(json.dumps({"backup": str(folder), "migration": manifest["migration"], "tables": len(manifest["counts"]), "application_rows": sum(count for name, count in manifest["counts"].items() if name != "__EFMigrationsHistory")}))
    except (OSError, ValueError, sqlite3.Error, TimeoutError):
        # Database diagnostics may contain private content. Failure must stop any dependent upgrade command.
        parser.exit(1, "Verified SQLite backup failed; do not continue an upgrade. Check the source, private destination, integrity and time budget.\n")


if __name__ == "__main__":
    main()
