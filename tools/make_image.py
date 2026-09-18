#!/usr/bin/env python3
"""Draw image.png, the plugin icon Jellyfin shows in its plugin catalogue.

Kept in the repo so the icon is reproducible rather than an unexplained binary.
Everything is drawn at 4x and downsampled, which is cheaper than fighting PIL's
non-antialiased primitives.

    python tools/make_image.py            # writes image.png
    python tools/make_image.py out.png    # writes somewhere else
"""

from __future__ import annotations

import sys

from PIL import Image, ImageDraw

SIZE = 512
SCALE = 4
S = SIZE * SCALE

# Deliberately not Jellyfin's own palette - this is the plugin's mark, not theirs.
TOP = (38, 120, 208)
BOTTOM = (96, 54, 168)


def background() -> Image.Image:
    """A rounded square with a vertical gradient."""
    gradient = Image.new("RGB", (1, S))
    for y in range(S):
        t = y / (S - 1)
        gradient.putpixel(
            (0, y),
            (
                round(TOP[0] + (BOTTOM[0] - TOP[0]) * t),
                round(TOP[1] + (BOTTOM[1] - TOP[1]) * t),
                round(TOP[2] + (BOTTOM[2] - TOP[2]) * t),
            ),
        )
    gradient = gradient.resize((S, S))

    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, S - 1, S - 1], radius=round(0.22 * S), fill=255
    )

    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    canvas.paste(gradient, (0, 0), mask)
    return canvas


def speaker(draw: ImageDraw.ImageDraw) -> None:
    """A speaker body plus cone, pointing right."""
    k = SCALE
    draw.rectangle([96 * k, 212 * k, 158 * k, 300 * k], fill=(255, 255, 255, 255))
    draw.polygon(
        [
            (158 * k, 212 * k),
            (232 * k, 140 * k),
            (232 * k, 372 * k),
            (158 * k, 300 * k),
        ],
        fill=(255, 255, 255, 255),
    )


def waves(canvas: Image.Image) -> None:
    """One arc only. A second, faded one turned to mush at catalogue size."""
    k = SCALE
    cx, cy = 232 * k, 256 * k
    radius = 56

    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    ImageDraw.Draw(layer).arc(
        [cx - radius * k, cy - radius * k, cx + radius * k, cy + radius * k],
        start=-54,
        end=54,
        fill=(255, 255, 255, 255),
        width=19 * k,
    )
    canvas.alpha_composite(layer)


def down_arrow(draw: ImageDraw.ImageDraw) -> None:
    """Says 'down' out loud, so the icon still reads at 32px."""
    k = SCALE
    x, y = 386 * k, 256 * k
    arm, thick = 52 * k, 21 * k

    draw.line([(x, y - arm), (x, y + arm - 4 * k)], fill=(255, 255, 255, 255), width=thick)
    draw.line(
        [(x - 40 * k, y + arm - 44 * k), (x, y + arm), (x + 40 * k, y + arm - 44 * k)],
        fill=(255, 255, 255, 255),
        width=thick,
        joint="curve",
    )


def main() -> int:
    out = sys.argv[1] if len(sys.argv) > 1 else "image.png"

    canvas = background()
    glyphs = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(glyphs)

    speaker(draw)
    down_arrow(draw)
    canvas.alpha_composite(glyphs)
    waves(canvas)

    canvas.resize((SIZE, SIZE), Image.LANCZOS).save(out, "PNG", optimize=True)
    print(f"wrote {out} ({SIZE}x{SIZE})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
