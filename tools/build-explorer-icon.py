#!/usr/bin/env python3
"""Build the Explorer ICO from Loudline's existing symbol using ImageMagick.

The source symbol is kept intact; a white dilation of its alpha is placed
behind it. Supersampling keeps the outline thin at small Windows icon sizes.
Requires ImageMagick (``magick`` or ``convert``) on PATH. No runtime dependency.
"""

import pathlib
import shutil
import struct
import subprocess
import tempfile


ROOT = pathlib.Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "Multiloud.App" / "Assets"
SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def build():
    command = shutil.which("magick") or shutil.which("convert")
    if not command:
        raise SystemExit("Install ImageMagick and put magick or convert on PATH.")
    images = []
    with tempfile.TemporaryDirectory(prefix="Loudline-explorer-icon-") as folder:
        temporary = pathlib.Path(folder)
        for size in SIZES:
            side = size * 4
            # 1 px at small sizes; 2 px at 256. The symbol's original
            # padding leaves enough room around the outline at every size.
            radius = max(4, round(size / 32))
            symbol = temporary / "symbol.png"
            mask = temporary / "outline.png"
            output = temporary / f"icon-{size}.png"
            subprocess.run([command, str(ASSETS / "AppIcon.png"), "-resize", f"{side}x{side}",
                str(symbol)], check=True)
            # The supplied raster contains almost-transparent stray pixels.
            # Ignore those only in the outline mask; keep the source untouched.
            subprocess.run([command, str(symbol), "-alpha", "extract", "-black-threshold", "5%", "-morphology", "Dilate",
                f"Disk:{radius}", str(mask)], check=True)
            subprocess.run([command, "-size", f"{side}x{side}", "xc:white", str(mask),
                "-alpha", "off", "-compose", "CopyOpacity", "-composite", str(symbol),
                "-compose", "Over", "-composite", "-resize", f"{size}x{size}",
                "-strip", "PNG32:" + str(output)], check=True)
            images.append(output.read_bytes())
            if size == 256:
                shutil.copyfile(output, ASSETS / "ExplorerIcon.png")

    offset = 6 + 16 * len(images)
    entries = []
    for size, image in zip(SIZES, images):
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0,
            1, 32, len(image), offset))
        offset += len(image)
    (ASSETS / "ExplorerIcon.ico").write_bytes(struct.pack("<HHH", 0, 1, len(images)) +
        b"".join(entries) + b"".join(images))
    print("Built ExplorerIcon.ico and ExplorerIcon.png from the unchanged AppIcon.png.")


if __name__ == "__main__":
    build()
