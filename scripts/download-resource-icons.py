#!/usr/bin/env python3
"""Bundle unchanged resource art for every supported tier/enchantment from Albion's Render Service.

Run explicitly to refresh the 133 icons; sources.json records URLs and SHA-256 hashes.
The service is not versioned, so a refresh can change bytes after a game update.
"""
import concurrent.futures
import datetime
import hashlib
import json
import urllib.request
import urllib.parse
from pathlib import Path

TARGET = Path(__file__).resolve().parent.parent / "AlbionDataAvalonia/Assets/Resources"


def download(name):
    url = f"https://render.albiononline.com/v1/item/{urllib.parse.quote(name, safe='')}.png?size=128&quality=1"
    with urllib.request.urlopen(url, timeout=30) as response:
        data = response.read()
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ValueError(f"Expected a PNG for {name}")
    (TARGET / f"{name}.png").write_bytes(data)
    return {"file": f"{name}.png", "url": url, "sha256": hashlib.sha256(data).hexdigest()}


def main():
    TARGET.mkdir(parents=True, exist_ok=True)
    names = [f"T{tier}_{family}" for family in ("WOOD", "ROCK", "HIDE", "FIBER", "ORE")
             for tier in range(1 if family in ("WOOD", "ROCK", "HIDE") else 2, 9)]
    names += [f"T{tier}_{family}_LEVEL{level}@{level}"
              for family in ("WOOD", "ROCK", "HIDE", "FIBER", "ORE")
              for tier in range(4, 9) for level in range(1, 4 if family == "ROCK" else 5)]
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        sources = list(pool.map(download, names))
    manifest = {"retrieved": datetime.date.today().isoformat(),
                "source": "Albion Online official Render Service", "assets": sources}
    (TARGET / "sources.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Downloaded {len(sources)} original resource icons")


if __name__ == "__main__":
    main()
