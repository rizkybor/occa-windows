"""Assembles Assets/occa.ico from the ico-<size>.png files rendered by render-windows-logo.swift (PNG-compressed ICO)."""
import os, struct, sys
assets = sys.argv[1]
sizes = [16, 24, 32, 48, 64, 128, 256]
images = [open(os.path.join(assets, f"ico-{s}.png"), "rb").read() for s in sizes]
header = struct.pack("<HHH", 0, 1, len(sizes))
offset = 6 + 16 * len(sizes)
entries = b""
for s, img in zip(sizes, images):
    entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(img), offset)
    offset += len(img)
with open(os.path.join(assets, "occa.ico"), "wb") as f:
    f.write(header + entries + b"".join(images))
for s in sizes:
    os.remove(os.path.join(assets, f"ico-{s}.png"))
print("occa.ico written")
