#!/usr/bin/env python3
"""Add a freshly built release to the Jellyfin plugin repository manifest.

Jellyfin reads manifest.json as a list of packages, each carrying a list of versions.
Pointing a Jellyfin server at the raw URL of that file makes every release installable
and updatable from the dashboard.

Run by the release workflow; also usable by hand:

    python tools/update_manifest.py \\
        --manifest manifest.json \\
        --build-yaml build.yaml \\
        --zip artifacts/jellytonedown_1.0.0.0.zip \\
        --version 1.0.0.0 \\
        --source-url https://github.com/owner/repo/releases/download/v1.0.0/jellytonedown_1.0.0.0.zip
"""

from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import json
import pathlib
import re
import sys


def read_build_yaml(path: pathlib.Path) -> dict:
    """Pull the handful of fields we need without taking a YAML dependency."""
    text = path.read_text(encoding="utf-8")
    fields = {}

    for key in ("name", "guid", "targetAbi", "owner", "category", "overview"):
        match = re.search(rf'^{key}:\s*"?([^"\n]+)"?\s*$', text, re.M)
        if match:
            fields[key] = match.group(1).strip()

    # Folded block scalars, e.g. description: > ... or changelog: > ...
    for key in ("description", "changelog"):
        match = re.search(rf"^{key}:\s*>\s*\n((?:[ \t]+.*\n|\n)*)", text, re.M)
        if match:
            body = " ".join(line.strip() for line in match.group(1).splitlines())
            fields[key] = re.sub(r"\s+", " ", body).strip()

    return fields


def md5_of(path: pathlib.Path) -> str:
    digest = hashlib.md5()  # noqa: S324 - Jellyfin verifies packages with MD5.
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=pathlib.Path)
    parser.add_argument("--build-yaml", required=True, type=pathlib.Path)
    parser.add_argument("--zip", required=True, type=pathlib.Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-url", required=True)
    args = parser.parse_args()

    if not args.zip.is_file():
        print(f"error: {args.zip} does not exist", file=sys.stderr)
        return 1

    meta = read_build_yaml(args.build_yaml)

    if args.manifest.is_file():
        packages = json.loads(args.manifest.read_text(encoding="utf-8"))
        if not isinstance(packages, list):
            print("error: manifest.json should contain a list", file=sys.stderr)
            return 1
    else:
        packages = []

    guid = meta.get("guid", "")
    package = next(
        (p for p in packages if str(p.get("guid", "")).lower() == guid.lower()),
        None,
    )

    if package is None:
        package = {
            "guid": guid,
            "name": meta.get("name", "Plugin"),
            "description": meta.get("description", ""),
            "overview": meta.get("overview", ""),
            "owner": meta.get("owner", ""),
            "category": meta.get("category", "General"),
            "versions": [],
        }
        packages.append(package)
    else:
        # Keep the descriptive fields in step with build.yaml.
        package["name"] = meta.get("name", package.get("name", ""))
        package["description"] = meta.get("description", package.get("description", ""))
        package["overview"] = meta.get("overview", package.get("overview", ""))
        package["owner"] = meta.get("owner", package.get("owner", ""))
        package["category"] = meta.get("category", package.get("category", "General"))

    entry = {
        "version": args.version,
        "changelog": meta.get("changelog", ""),
        "targetAbi": meta.get("targetAbi", "12.0.0.0"),
        "sourceUrl": args.source_url,
        "checksum": md5_of(args.zip),
        "timestamp": _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    versions = [v for v in package.get("versions", []) if v.get("version") != args.version]
    versions.insert(0, entry)
    package["versions"] = versions

    args.manifest.write_text(
        json.dumps(packages, indent=4, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    print(f"manifest.json now lists {len(versions)} version(s) of {package['name']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
