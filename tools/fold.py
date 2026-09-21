#!/usr/bin/env python3
"""Hex fold script for TortalPortal Lite.

Builds and packages the local HexiumDist release archive.
Adheres strictly to the HexiumPublishing specification:
- Forward slashes only in zip entries (plugins/TortalPortalLite.dll)
- Deterministic 1980-01-01 timestamps for reproducible builds
- Verification of version consistency and DLL SHA256 matching
"""

import hashlib
import json
import pathlib
import re
import sys
import zipfile

FIXED_DATE = (1980, 1, 1, 0, 0, 0)
PACKAGE_FILES = ["manifest.json", "README.md", "CHANGELOG.md", "icon.png"]


def main() -> int:
    repo_root = pathlib.Path(__file__).resolve().parent.parent
    dist_dir = repo_root / "HexiumDist"
    manifest_path = dist_dir / "manifest.json"
    plugin_cs_path = repo_root / "Plugin.cs"
    csproj_path = repo_root / "TortalPortalLite.csproj"
    features_doc = repo_root / "docs" / "FEATURES.md"
    release_dll = repo_root / "bin" / "Release" / "net472" / "TortalPortalLite.dll"
    staged_dll = dist_dir / "plugins" / "TortalPortalLite.dll"

    print("=== Hex Fold: TortalPortal Lite ===")

    # 1. Read and verify version from manifest.json
    if not manifest_path.is_file():
        print(f"ERROR: Missing manifest at {manifest_path}", file=sys.stderr)
        return 1

    manifest_data = json.loads(manifest_path.read_text(encoding="utf-8"))
    ver = manifest_data.get("version_number", "").strip()
    if not ver:
        print("ERROR: manifest.json has no version_number", file=sys.stderr)
        return 1

    print(f"[1/5] Target version: v{ver}")

    # Check Plugin.cs
    plugin_match = re.search(r'public const string ModVersion = "([^"]+)";', plugin_cs_path.read_text(encoding="utf-8"))
    if not plugin_match or plugin_match.group(1) != ver:
        print(f"ERROR: Plugin.cs ModVersion ({plugin_match.group(1) if plugin_match else 'None'}) != {ver}", file=sys.stderr)
        return 1

    # Check csproj
    csproj_match = re.search(r'<Version>([^<]+)</Version>', csproj_path.read_text(encoding="utf-8"))
    if not csproj_match or csproj_match.group(1) != ver:
        print(f"ERROR: csproj Version ({csproj_match.group(1) if csproj_match else 'None'}) != {ver}", file=sys.stderr)
        return 1

    # Check docs/FEATURES.md
    if features_doc.is_file():
        feat_match = re.search(r'\*\*Version:\*\*\s*([0-9\.]+)', features_doc.read_text(encoding="utf-8"))
        if not feat_match or feat_match.group(1) != ver:
            print(f"WARNING: docs/FEATURES.md version ({feat_match.group(1) if feat_match else 'None'}) != {ver}")

    print("      Version consistency verified across all project files.")

    # 2. Stage DLL
    print("[2/5] Staging Release DLL...")
    if not release_dll.is_file():
        print(f"ERROR: Built DLL not found at {release_dll}. Run dotnet build -c Release first.", file=sys.stderr)
        return 1

    staged_dll.parent.mkdir(parents=True, exist_ok=True)
    dll_bytes = release_dll.read_bytes()
    if not staged_dll.is_file() or staged_dll.read_bytes() != dll_bytes:
        staged_dll.write_bytes(dll_bytes)
        print("      Copied built DLL to HexiumDist/plugins/TortalPortalLite.dll")
    else:
        print("      HexiumDist/plugins/TortalPortalLite.dll is already up to date.")

    dll_sha256 = hashlib.sha256(dll_bytes).hexdigest()
    print(f"      DLL SHA256: {dll_sha256}")

    # 3. Clean up superseded zip files
    print("[3/5] Cleaning superseded archives...")
    for old_zip in dist_dir.glob("TortalPortalLite-v*.zip"):
        print(f"      Removing old archive: {old_zip.name}")
        old_zip.unlink()

    # 4. Pack the release zip
    zip_name = f"TortalPortalLite-v{ver}-hexium.zip"
    zip_path = dist_dir / zip_name
    print(f"[4/5] Packing {zip_name}...")

    entries = []
    for fname in PACKAGE_FILES:
        fpath = dist_dir / fname
        if not fpath.is_file():
            print(f"ERROR: Missing package file: {fpath}", file=sys.stderr)
            return 1
        entries.append((fname, fpath))
    entries.append(("plugins/TortalPortalLite.dll", staged_dll))

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for arcname, fpath in entries:
            assert "\\" not in arcname, f"Backslash detected in arcname: {arcname}"
            info = zipfile.ZipInfo(arcname, date_time=FIXED_DATE)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            zf.writestr(info, fpath.read_bytes())

    print(f"      Wrote {len(entries)} entries with fixed 1980 timestamps.")

    # 5. Verify packed zip
    print(f"[5/5] Verifying archive {zip_name}...")
    with zipfile.ZipFile(zip_path, "r") as zf:
        namelist = zf.namelist()
        for name in namelist:
            if "\\" in name:
                print(f"ERROR: Backslash in zip entry: {name}", file=sys.stderr)
                return 1

        if "plugins/TortalPortalLite.dll" not in namelist:
            print("ERROR: plugins/TortalPortalLite.dll missing from archive!", file=sys.stderr)
            return 1

        packed_dll_bytes = zf.read("plugins/TortalPortalLite.dll")
        packed_sha256 = hashlib.sha256(packed_dll_bytes).hexdigest()
        if packed_sha256 != dll_sha256:
            print(f"ERROR: Packed DLL SHA256 mismatch! {packed_sha256} != {dll_sha256}", file=sys.stderr)
            return 1

        zip_size = zip_path.stat().st_size
        print(f"      Verified {len(namelist)} entries. Size: {zip_size:,} bytes.")
        print(f"      Packed DLL hash matches bin/Release ({packed_sha256[:16]}...)")

    print(f"\nHex fold complete: {zip_path}")
    print("Nothing was published. hex web is a separate, explicit step.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
