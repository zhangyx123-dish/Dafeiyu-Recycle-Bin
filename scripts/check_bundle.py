#!/usr/bin/env python3
"""Read-only bundle check and truthful local-host gate; never performs recycling."""
import argparse
import hashlib
import json
import os
import platform
import struct
import sys
import wave
from pathlib import Path


def inspect(root):
    manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
    errors = []
    for entry in manifest["files"]:
        path = (root / entry["path"]).resolve()
        if not path.is_relative_to(root.resolve()):
            errors.append("UNSAFE_MANIFEST_PATH")
            continue
        if not path.is_file():
            errors.append("MISSING:" + entry["path"])
        elif hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
            errors.append("HASH_MISMATCH:" + entry["path"])
    for name in ("frog.png", "maid.png", "maid-swallow.png"):
        path = root / "assets" / name
        if path.exists():
            data = path.read_bytes()
            if len(data) < 33 or data[:8] != b"\x89PNG\r\n\x1a\n":
                errors.append("INVALID_PNG:" + name)
            else:
                width, height = struct.unpack(">II", data[16:24])
                if width % 3 or height % 2 or data[25] != 6:
                    errors.append("SPRITE_GRID_OR_ALPHA:" + name)
    for name in ("voice.wav", "token.wav"):
        path = root / "assets" / name
        if not path.is_file():
            errors.append("MISSING_AUDIO:" + name)
            continue
        with wave.open(str(path), "rb") as sound:
            if not sound.getnframes() or sound.getsampwidth() != 2:
                errors.append("INVALID_AUDIO:" + name)
    return errors


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle-only", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    try:
        errors = inspect(root)
    except (OSError, ValueError, KeyError, wave.Error) as exc:
        errors = ["BUNDLE_CHECK_ERROR:" + str(exc)]
    windows = platform.system() == "Windows" and os.name == "nt"
    result = {
        "bundle": "FAIL" if errors else "PASS",
        "host": "WINDOWS" if windows else "WINDOWS_REQUIRED",
        "native_compile": "NOT_RUN",
        "recycle_test": "NOT_RUN",
        "files_processed": 0,
        "errors": errors,
    }
    print(json.dumps(result, ensure_ascii=False))
    return 1 if errors else 0 if args.bundle_only or windows else 2


if __name__ == "__main__":
    sys.exit(main())
