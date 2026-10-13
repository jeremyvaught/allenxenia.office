#!/usr/bin/env python3
"""Build or verify a portable release without tenant data or runtime credentials."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tempfile
import uuid

REQUIRED = {"AllenBuilding.Web.dll", "AllenBuilding.Web.deps.json", "AllenBuilding.Web.runtimeconfig.json", "appsettings.json", "wwwroot/css/app.css"}
ROOT_JSON = {"appsettings.json", "AllenBuilding.Web.deps.json", "AllenBuilding.Web.runtimeconfig.json", "AllenBuilding.Web.staticwebassets.endpoints.json"}
STATIC_SUFFIXES = {".css", ".js", ".ico", ".png", ".jpg", ".jpeg", ".webp", ".svg", ".woff", ".woff2", ".ttf", ".txt", ".gz", ".br"}
MAX_FILE = 128 * 1024 * 1024
MAX_TOTAL = 1024 * 1024 * 1024


def safe_path(name):
    if not isinstance(name, str) or len(name) > 512 or "\\" in name or any(ord(c) < 32 for c in name):
        raise ValueError("Invalid release path.")
    path = PurePosixPath(name)
    if path.is_absolute() or path.as_posix() != name or any(p.startswith(".") for p in path.parts):
        raise ValueError("Invalid release path.")
    if len(path.parts) == 1:
        if name == "manifest.json" or name in ROOT_JSON or path.suffix in {".dll", ".pdb"} or name == "web.config":
            return path
    elif path.parts[0] == "wwwroot" and path.suffix in STATIC_SUFFIXES:
        return path
    elif path.parts[0] == "runtimes" and path.suffix in {".dll", ".so", ".dylib", ".a"}:
        return path
    raise ValueError("Unexpected or private file in release.")


def inventory(directory):
    directory = Path(directory)
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("Release must be a real directory.")
    result = {}; total = 0
    for base, folders, files in os.walk(directory, followlinks=False):
        for folder in folders:
            child = Path(base) / folder
            relative = child.relative_to(directory).parts
            if child.is_symlink() or any(p.startswith(".") for p in relative) or relative[0] not in {"wwwroot", "runtimes"}:
                raise ValueError("Unexpected or linked release directory.")
        for filename in files:
            child = Path(base) / filename; name = child.relative_to(directory).as_posix()
            safe_path(name)
            if child.is_symlink() or not child.is_file():
                raise ValueError("Linked or nonregular release file.")
            if name == "manifest.json":
                continue
            size = child.stat().st_size
            total += size
            if size > MAX_FILE or total > MAX_TOTAL or len(result) >= 5000:
                raise ValueError("Release exceeds bounded size.")
            digest = hashlib.sha256()
            with child.open("rb") as stream:
                for chunk in iter(lambda: stream.read(131072), b""):
                    digest.update(chunk)
            result[name] = {"bytes": size, "sha256": digest.hexdigest()}
    if not REQUIRED.issubset(result):
        raise ValueError("Required release files are missing.")
    # The only packaged settings are the deliberately public base profile.
    settings = json.loads((directory / "appsettings.json").read_text(encoding="utf-8"))
    if not isinstance(settings, dict) or set(settings) != {"Database", "Logging", "AllowedHosts"} or settings["Database"] != {"Provider": "MySql"}:
        raise ValueError("The base profile must contain only public production defaults.")
    if not isinstance(settings["AllowedHosts"], str) or not isinstance(settings["Logging"], dict) or set(settings["Logging"]) != {"LogLevel"}:
        raise ValueError("Invalid public base profile.")
    levels = settings["Logging"]["LogLevel"]
    if not isinstance(levels, dict) or any(not isinstance(k, str) or len(k) > 100 or v not in {"Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"} for k, v in levels.items()):
        raise ValueError("Invalid public logging defaults.")
    runtime = json.loads((directory / "AllenBuilding.Web.runtimeconfig.json").read_text(encoding="utf-8"))
    if runtime.get("runtimeOptions", {}).get("tfm") != "net10.0":
        raise ValueError("Unexpected release framework.")
    return dict(sorted(result.items()))


def seal(directory, revision):
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("A full source revision is required.")
    entries = inventory(directory)
    manifest = {"format": 1, "source_revision": revision, "framework": "net10.0", "created_at": datetime.datetime.now(datetime.timezone.utc).isoformat(), "files": entries}
    with (Path(directory) / "manifest.json").open("x", encoding="utf-8") as stream:
        json.dump(manifest, stream, indent=2, sort_keys=True); stream.write("\n")
        stream.flush(); os.fsync(stream.fileno())
    return manifest


def verify(directory):
    directory = Path(directory)
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("Release must be a real directory.")
    manifest_path = directory / "manifest.json"
    if manifest_path.is_symlink() or not manifest_path.is_file() or manifest_path.stat().st_size > 1024 * 1024:
        raise ValueError("Missing or invalid manifest.")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if not isinstance(manifest, dict) or manifest.get("format") != 1 or manifest.get("framework") != "net10.0" or not re.fullmatch(r"[0-9a-f]{40}", str(manifest.get("source_revision", ""))):
        raise ValueError("Invalid release manifest.")
    entries = manifest.get("files")
    if not isinstance(entries, dict) or len(entries) > 5000:
        raise ValueError("Invalid manifest inventory.")
    for name, entry in entries.items():
        safe_path(name)
        if not isinstance(entry, dict) or set(entry) != {"bytes", "sha256"} or type(entry["bytes"]) is not int or not 0 <= entry["bytes"] <= MAX_FILE or not re.fullmatch(r"[0-9a-f]{64}", str(entry["sha256"])):
            raise ValueError("Invalid manifest entry.")
    if inventory(directory) != entries:
        raise ValueError("Release differs from its manifest.")
    return manifest


def clean_revision(repository):
    if subprocess.check_output(["git", "status", "--porcelain"], cwd=repository, stderr=subprocess.DEVNULL).strip():
        raise ValueError("Commit the verified source before packaging.")
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository, stderr=subprocess.DEVNULL).decode().strip()
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("Invalid source revision.")
    return revision


def build(repository):
    repository = Path(repository).resolve(strict=True)
    revision = clean_revision(repository)
    root = repository / "artifacts" / "releases"
    if (repository / "artifacts").is_symlink() or root.is_symlink():
        raise ValueError("Use an owned local artifact directory.")
    root.mkdir(parents=True, exist_ok=True, mode=0o700)
    pending = Path(tempfile.mkdtemp(prefix=".pending-", dir=root))
    published = root / ("release-" + revision[:12] + "-" + uuid.uuid4().hex[:8])
    try:
        # Rebuild ignored CSS, then publish the DLL and bundled native dependencies without a host-specific executable.
        for command in (["npm", "run", "build:css"], ["dotnet", "publish", "src/AllenBuilding.Web", "--configuration", "Release", "--no-restore", "-p:UseAppHost=false", "--output", str(pending)]):
            subprocess.run(command, cwd=repository, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=300)
        if clean_revision(repository) != revision:
            raise ValueError("Source changed while packaging.")
        seal(pending, revision); verify(pending)
        pending.rename(published)
        return published
    finally:
        # Remove only this invocation's unpublished directory, never another release.
        if pending.exists():
            shutil.rmtree(pending)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="operation", required=True)
    sub.add_parser("build")
    check = sub.add_parser("verify"); check.add_argument("directory")
    args = parser.parse_args()
    try:
        if args.operation == "build":
            folder = build(Path(__file__).resolve().parent.parent); manifest = verify(folder)
        else:
            folder = Path(args.directory); manifest = verify(folder)
        print(json.dumps({"release": str(folder), "source_revision": manifest["source_revision"], "files": len(manifest["files"]), "integrity": "verified"}))
    except (OSError, ValueError, subprocess.SubprocessError, TypeError, KeyError):
        parser.exit(1, "Release operation failed. Check clean committed source, tools, public-only files and manifest integrity; no existing release was replaced.\n")


if __name__ == "__main__":
    main()
