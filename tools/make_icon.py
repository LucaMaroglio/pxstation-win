"""Crea src/WindowsSpiceClient/Assets/app.ico dai PNG dell'icona della versione Linux.

Il formato ICO ammette voci PNG (Windows Vista e successivi): ogni dimensione è copiata
così com'è, senza ricampionare. 256 è la dimensione massima di una voce ICO.

    python tools/make_icon.py
"""

from __future__ import annotations

import os
import struct

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SOURCE = os.path.join(ROOT, "..", "linux-spice-client", "data", "icons", "hicolor")
TARGET = os.path.join(ROOT, "src", "WindowsSpiceClient", "Assets", "app.ico")
SIZES = (16, 32, 64, 128, 256)


def png_size(data: bytes) -> tuple[int, int]:
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("non è un PNG")
    return struct.unpack(">II", data[16:24])


def main():
    images = []
    for size in SIZES:
        path = os.path.join(SOURCE, f"{size}x{size}", "apps", "linux-spice-client.png")
        with open(path, "rb") as handle:
            data = handle.read()
        if png_size(data) != (size, size):
            raise ValueError(f"{path}: dimensioni inattese {png_size(data)}")
        images.append((size, data))

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries, payload = b"", b""
    for size, data in images:
        dim = 0 if size >= 256 else size  # 0 = 256 nel formato ICO
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset + len(payload))
        payload += data
    os.makedirs(os.path.dirname(TARGET), exist_ok=True)
    with open(TARGET, "wb") as handle:
        handle.write(header + entries + payload)
    print(f"{TARGET}: {len(images)} dimensioni, {len(header + entries + payload)} byte")


if __name__ == "__main__":
    main()
