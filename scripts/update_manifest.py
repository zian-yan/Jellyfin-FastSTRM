#!/usr/bin/env python3
"""Insert (or replace) the just-released version in manifest.json.

Reads:  meta.json (version + targetAbi), RELEASE_NOTES.md (changelog)
Env:    RELEASE_TAG (e.g. 3.4.1), CHECKSUM (MD5 of FastSTRM.zip)
"""

import datetime
import json
import os

REPO = "zian-yan/jellyfin-plugin-faststrm"


def main() -> None:
    # 标签只带版本号，兼容历史 v 前缀
    tag = os.environ["RELEASE_TAG"].removeprefix("v")
    checksum = os.environ.get("CHECKSUM", "")

    with open("meta.json", encoding="utf-8") as handle:
        meta = json.load(handle)

    version = meta["version"]
    target_abi = meta["targetAbi"]

    changelog = ""
    if os.path.exists("RELEASE_NOTES.md"):
        with open("RELEASE_NOTES.md", encoding="utf-8") as handle:
            changelog = handle.read().strip()

    with open("manifest.json", encoding="utf-8") as handle:
        manifest = json.load(handle)

    plugin = manifest[0]
    versions = [item for item in plugin["versions"] if item.get("version") != version]
    versions.insert(
        0,
        {
            "version": version,
            "changelog": changelog,
            "targetAbi": target_abi,
            "sourceUrl": f"https://github.com/{REPO}/releases/download/{tag}/FastSTRM.zip",
            "checksum": checksum,
            "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime(
                "%Y-%m-%dT%H:%M:%SZ"
            ),
        },
    )
    versions.sort(key=lambda item: [int(part) for part in item["version"].split(".")], reverse=True)
    plugin["versions"] = versions

    with open("manifest.json", "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)
        handle.write("\n")

    print(f"manifest.json updated: {version} (checksum={checksum or 'none'})")


if __name__ == "__main__":
    main()
