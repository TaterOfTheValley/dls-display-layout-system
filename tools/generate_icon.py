"""Build the multi-size Windows icon. Requires Pillow: pip install Pillow.

The coordinates below are the source artwork. Run from any directory; the .ico is
written beside DLS.csproj so the compiler and Velopack can use the same file.
"""

from io import BytesIO
from pathlib import Path
import struct

from PIL import Image, ImageDraw


OUTPUT = Path(__file__).resolve().parents[1] / "src" / "Assets" / "dls.ico"
SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def draw_icon(size: int) -> Image.Image:
    # Render each frame at four times its final size so edges stay smooth while
    # the monitor shapes remain aligned at every Windows icon size.
    scale = size / 64 * 4
    canvas = Image.new("RGBA", (size * 4, size * 4), (0, 0, 0, 0))
    pen = ImageDraw.Draw(canvas)

    def box(x0, y0, x1, y1):
        return tuple(round(v * scale) for v in (x0, y0, x1, y1))

    def radius(value):
        return round(value * scale)

    # The charcoal tile is legible on both light and dark taskbars. The two
    # offset screens make the subject a display *layout*, rather than one PC.
    pen.rounded_rectangle(box(2, 2, 62, 62), radius=radius(13),
                          fill="#24211c", outline="#795b32", width=max(1, radius(1)))

    # Secondary display, slightly behind and lower than the main display.
    pen.rounded_rectangle(box(34, 22, 57, 43), radius=radius(3), fill="#b98b45")
    pen.rounded_rectangle(box(37, 25, 54, 39), radius=radius(1), fill="#312c23")
    pen.rectangle(box(43, 43, 48, 50), fill="#b98b45")
    pen.rounded_rectangle(box(39, 50, 52, 53), radius=radius(1), fill="#b98b45")

    # Primary display and its brighter gold border are the visual anchor.
    pen.rounded_rectangle(box(7, 13, 39, 43), radius=radius(4), fill="#e8bd63")
    pen.rounded_rectangle(box(10, 16, 36, 39), radius=radius(2), fill="#383026")
    pen.rounded_rectangle(box(12, 18, 34, 37), radius=radius(1), fill="#665033")
    pen.polygon([tuple(round(v * scale) for v in p)
                 for p in ((12, 18), (34, 18), (12, 32))], fill="#80643d")
    pen.rectangle(box(20, 43, 26, 51), fill="#e8bd63")
    pen.rounded_rectangle(box(15, 51, 31, 54), radius=radius(1), fill="#e8bd63")

    return canvas.resize((size, size), Image.Resampling.LANCZOS)


def main() -> None:
    frames = []
    for size in SIZES:
        data = BytesIO()
        draw_icon(size).save(data, format="PNG")
        frames.append(data.getvalue())

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    offset = 6 + len(frames) * 16
    with OUTPUT.open("wb") as icon:
        icon.write(struct.pack("<HHH", 0, 1, len(frames)))
        for size, data in zip(SIZES, frames):
            icon.write(struct.pack("<BBBBHHII", size % 256, size % 256,
                                   0, 0, 1, 32, len(data), offset))
            offset += len(data)
        for data in frames:
            icon.write(data)

    print(OUTPUT)


if __name__ == "__main__":
    main()
