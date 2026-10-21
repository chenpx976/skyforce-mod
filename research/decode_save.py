#!/usr/bin/env python3
"""解码 Sky Force Reloaded 的 Save/SaveGame.dat（UTF-16LE JSON + XOR/hex 键值）。"""
import json
import re
import sys

KEY = bytes([122, 16, 44, 66, 50, 112])


def decrypt(value: str) -> str:
    if not re.fullmatch(r"[0-9A-F]{2}(-[0-9A-F]{2})*", value):
        return value
    raw = bytes(int(part, 16) for part in value.split("-"))
    plain = bytes(b ^ KEY[i % len(KEY)] for i, b in enumerate(raw))
    try:
        return plain.decode("utf-16-le")
    except UnicodeDecodeError:
        return plain.decode("utf-8", "replace")


def load(path: str) -> dict[str, str]:
    data = open(path, "rb").read().decode("utf-16-le")
    return {decrypt(k): decrypt(v) for k, v in json.loads(data).items()}


if __name__ == "__main__":
    save = load(sys.argv[1] if len(sys.argv) > 1 else "research/save/SaveGame.dat")
    for key in sorted(save):
        print(f"{key}\t{save[key]}")
