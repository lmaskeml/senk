"""Build Windows .ico and Android launcher mipmaps from senk.jpg."""
from __future__ import annotations

import math
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "senk.jpg"
WIN_ASSETS = ROOT / "src" / "AndroidManager.Shell" / "Assets"
ANDROID_RES = ROOT / "AndroidCompanion" / "app" / "src" / "main" / "res"

# Circle measured from senk.jpg (1024x1024, checkerboard outside).
CX, CY, RADIUS = 506.0, 512.0, 384.0

MIPMAPS = {
    "mipmap-mdpi": 48,
    "mipmap-hdpi": 72,
    "mipmap-xhdpi": 96,
    "mipmap-xxhdpi": 144,
    "mipmap-xxxhdpi": 192,
}


def cutout(src: Image.Image) -> Image.Image:
    im = src.convert("RGBA")
    px = im.load()
    w, h = im.size
    for y in range(h):
        for x in range(w):
            d = math.hypot(x - CX, y - CY)
            r, g, b, _ = px[x, y]
            if d > RADIUS + 1.5:
                px[x, y] = (0, 0, 0, 0)
            elif d > RADIUS:
                a = int(round(255 * (1.0 - (d - RADIUS) / 1.5)))
                px[x, y] = (r, g, b, max(0, min(255, a)))
    return im


def resize(im: Image.Image, size: int) -> Image.Image:
    return im.resize((size, size), Image.Resampling.LANCZOS)


def write_ico(im: Image.Image, dest: Path) -> None:
    """Multi-size ICO with PNG payloads (Windows Vista+)."""
    import io
    import struct

    sizes = [16, 24, 32, 48, 64, 128, 256]
    payloads: list[bytes] = []
    for size in sizes:
        buf = io.BytesIO()
        resize(im, size).save(buf, format="PNG")
        payloads.append(buf.getvalue())

    count = len(sizes)
    offset = 6 + 16 * count
    header = struct.pack("<HHH", 0, 1, count)
    entries = b""
    blobs = b""
    for size, data in zip(sizes, payloads):
        w = 0 if size == 256 else size
        entries += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    dest.write_bytes(header + entries + blobs)


def write_android(im: Image.Image) -> None:
    for folder, size in MIPMAPS.items():
        d = ANDROID_RES / folder
        d.mkdir(parents=True, exist_ok=True)
        png = resize(im, size)
        png.save(d / "ic_launcher.png", format="PNG")
        png.save(d / "ic_launcher_round.png", format="PNG")

    # Adaptive foreground: 108dp at xxxhdpi = 432px, logo in the inner ~72dp.
    fg_size = 432
    inner = int(432 * 72 / 108)
    logo = resize(im, inner)
    fg = Image.new("RGBA", (fg_size, fg_size), (0, 0, 0, 0))
    off = (fg_size - inner) // 2
    fg.paste(logo, (off, off), logo)
    drawable = ANDROID_RES / "drawable"
    drawable.mkdir(parents=True, exist_ok=True)
    fg.save(drawable / "ic_launcher_foreground.png", format="PNG")

    (ANDROID_RES / "values").mkdir(parents=True, exist_ok=True)
    (ANDROID_RES / "values" / "ic_launcher_colors.xml").write_text(
        """<?xml version="1.0" encoding="utf-8"?>
<resources>
    <color name="ic_launcher_background">#3BA3D9</color>
</resources>
""",
        encoding="utf-8",
    )

    anydpi = ANDROID_RES / "mipmap-anydpi-v26"
    anydpi.mkdir(parents=True, exist_ok=True)
    adaptive = """<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@color/ic_launcher_background" />
    <foreground android:drawable="@drawable/ic_launcher_foreground" />
</adaptive-icon>
"""
    (anydpi / "ic_launcher.xml").write_text(adaptive, encoding="utf-8")
    (anydpi / "ic_launcher_round.xml").write_text(adaptive, encoding="utf-8")


def main() -> None:
    if not SRC.exists():
        raise SystemExit(f"Missing {SRC}")
    logo = cutout(Image.open(SRC))
    WIN_ASSETS.mkdir(parents=True, exist_ok=True)
    logo.save(WIN_ASSETS / "app.png", format="PNG")
    write_ico(logo, WIN_ASSETS / "app.ico")
    write_android(logo)
    print(f"Wrote {WIN_ASSETS / 'app.ico'}")
    print(f"Wrote {WIN_ASSETS / 'app.png'}")
    print(f"Wrote Android mipmaps under {ANDROID_RES}")


if __name__ == "__main__":
    main()
