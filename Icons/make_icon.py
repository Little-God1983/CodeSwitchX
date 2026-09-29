"""Makes src/CodeSwitchX.UI/Assets/CodeSwitchX.ico from the S tile of the Grok logo.

Crops the tile (dropping the CodeSwitchX wordmark under it), makes its rounded corners transparent and writes
every size Windows asks for, 16 to 256 px. Needs Pillow: python -m pip install pillow

    python Icons/make_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

HERE = Path(__file__).resolve().parent
SOURCE = HERE / "grok-image-e7ed6691-d7f1-49bb-9c57-d1bd56036ad5.jpg"
TARGET = HERE.parent / "src" / "CodeSwitchX.UI" / "Assets" / "CodeSwitchX.ico"

# The tile's faint border runs x 456..822, y 89..460 in the 1280x720 source; its corner radius is about 0.185 of a side.
TILE_BOX = (456, 89, 823, 461)
CORNER = 0.185
MASTER = 1024
SIZES = [256, 128, 64, 48, 40, 32, 24, 20, 16]


def main() -> None:
    tile = Image.open(SOURCE).convert("RGB").crop(TILE_BOX).resize((MASTER, MASTER), Image.LANCZOS)

    # Rounded-corner alpha, drawn at 4x and scaled down for a smooth edge.
    scale = 4
    mask = Image.new("L", (MASTER * scale, MASTER * scale), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (2 * scale, 2 * scale, (MASTER - 2) * scale - 1, (MASTER - 2) * scale - 1),
        radius=int(CORNER * MASTER * scale), fill=255)
    master = tile.convert("RGBA")
    master.putalpha(mask.resize((MASTER, MASTER), Image.LANCZOS))

    frames = []
    for size in SIZES:
        frame = master.resize((size, size), Image.LANCZOS)
        if size <= 32:
            # A touch of sharpening so the S keeps a crisp edge after the large reduction.
            rgb = frame.convert("RGB").filter(ImageFilter.UnsharpMask(radius=0.6, percent=60, threshold=2))
            rgb.putalpha(frame.getchannel("A"))
            frame = rgb
        frames.append(frame)

    frames[0].save(TARGET, format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[1:])
    print(f"wrote {TARGET}")


if __name__ == "__main__":
    main()
