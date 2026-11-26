#!/usr/bin/env python3
"""Draws the xJournal icon: a purple rounded square with a white open book and a small pen.

Usage: python3 tools/make_icon.py   (requires Pillow)
Writes src/XJournal.App/Assets/xjournal.ico (16-256 px) and xjournal.png (512 px).
"""
import math
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

MASTER = 1024
OUT = Path(__file__).resolve().parent.parent / "src" / "XJournal.App" / "Assets"

TOP, BOTTOM = (108, 84, 232), (58, 40, 156)          # background gradient
PAGE, PAGE_SHADE = (255, 255, 255), (226, 221, 250)  # book pages
COVER = (38, 26, 110)                                # book edge under the pages
LINE = (196, 186, 240)                               # text lines on the page
PEN, PEN_DARK, NIB = (255, 196, 72), (226, 150, 30), (52, 36, 120)


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def bezier(p0, p1, p2, steps=48):
    return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t ** 2 * p2[0],
             (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t ** 2 * p2[1])
            for t in (i / steps for i in range(steps + 1))]


def page(side, top, bottom, sag, dx=0, dy=0):
    """A page from the spine (x=512) to the outer edge, with curved top and bottom edges."""
    outer = 512 + side * 292
    spine_top, spine_bottom = (512 + dx, top + 40 + dy), (512 + dx, bottom + 44 + dy)
    outer_top, outer_bottom = (outer + dx, top + dy), (outer + dx, bottom + dy)
    mid = 512 + side * 150
    upper = bezier(spine_top, (mid + dx, top - sag + dy), outer_top)
    lower = bezier(outer_bottom, (mid + dx, bottom - sag + 40 + dy), spine_bottom)
    return upper + lower


def draw(simple):
    img = Image.new("RGBA", (MASTER, MASTER), (0, 0, 0, 0))

    # Background: vertical gradient clipped to a rounded square, plus a soft top highlight.
    gradient = Image.new("RGBA", (MASTER, MASTER))
    gd = ImageDraw.Draw(gradient)
    for y in range(MASTER):
        gd.line([(0, y), (MASTER, y)], fill=lerp(TOP, BOTTOM, y / MASTER) + (255,))
    mask = Image.new("L", (MASTER, MASTER), 0)
    inset = 40 if simple else 64
    ImageDraw.Draw(mask).rounded_rectangle([inset, inset, MASTER - inset, MASTER - inset], radius=210, fill=255)
    img.paste(gradient, (0, 0), mask)
    highlight = Image.new("RGBA", (MASTER, MASTER), (0, 0, 0, 0))
    ImageDraw.Draw(highlight).ellipse([-260, -700, MASTER + 260, 380], fill=(255, 255, 255, 30))
    highlight = highlight.filter(ImageFilter.GaussianBlur(90))
    img.alpha_composite(Image.composite(highlight, Image.new("RGBA", (MASTER, MASTER)), mask))

    d = ImageDraw.Draw(img)
    top, bottom, sag = (300, 690, 50) if simple else (330, 700, 56)

    # Book: the cover edge peeking out below, then the two pages.
    for side in (-1, 1):
        d.polygon(page(side, top, bottom, sag, dy=30), fill=COVER)
    d.polygon(page(-1, top, bottom, sag), fill=PAGE)
    d.polygon(page(1, top, bottom, sag), fill=PAGE)
    # Gentle shading toward the spine.
    for i in range(18):
        w = 3 if simple else 2
        shade = lerp(PAGE_SHADE, PAGE, i / 18) + (255,)
        for side in (-1, 1):
            x = 512 + side * (i * 4 + 2)
            d.line([(x, top + 40 - i * 1.6), (x, bottom + 44 - i * 1.6)], fill=shade, width=w * 2)
    d.line([(512, top + 40), (512, bottom + 44)], fill=PAGE_SHADE, width=10 if simple else 6)

    if simple:
        return img

    # Text lines on the left page, following the page curve.
    for row in range(4):
        y0 = top + 110 + row * 74
        pts = bezier((292, y0), (390, y0 - sag * 0.55), (462, y0 + 22))
        d.line(pts, fill=LINE, width=16, joint="curve")

    # Pen lying across the right page, nib pointing down-left.
    angle = math.radians(-52)
    ux, uy = math.cos(angle), math.sin(angle)   # along the pen (toward the top end)
    nx, ny = -uy, ux                            # across the pen
    tip = (560, 712)
    length, width, nib_len = 430, 64, 120

    def at(along, across):
        return (tip[0] + ux * along + nx * across, tip[1] + uy * along + ny * across)

    shadow = Image.new("RGBA", (MASTER, MASTER), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    sd.polygon([at(0, 0), at(nib_len, -width / 2), at(length, -width / 2), at(length, width / 2), at(nib_len, width / 2)],
               fill=(20, 10, 60, 90))
    shadow = shadow.filter(ImageFilter.GaussianBlur(14))
    img.alpha_composite(shadow, (10, 16))

    d.polygon([at(nib_len, -width / 2), at(length, -width / 2), at(length, width / 2), at(nib_len, width / 2)], fill=PEN)
    d.polygon([at(nib_len, 0), at(length, 0), at(length, width / 2), at(nib_len, width / 2)], fill=PEN_DARK)
    d.ellipse([at(length, 0)[0] - width / 2, at(length, 0)[1] - width / 2,
               at(length, 0)[0] + width / 2, at(length, 0)[1] + width / 2], fill=PEN)
    d.polygon([at(nib_len + 26, -width / 2), at(nib_len + 50, -width / 2), at(nib_len + 50, width / 2), at(nib_len + 26, width / 2)],
              fill=PEN_DARK)
    d.polygon([at(0, 0), at(nib_len, -width / 2), at(nib_len, width / 2)], fill=NIB)
    d.line([at(10, 0), at(nib_len - 30, 0)], fill=(150, 130, 230), width=6)
    return img


def main():
    detailed, simple = draw(False), draw(True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [(simple if s <= 24 else detailed).resize((s, s), Image.LANCZOS) for s in sizes]
    OUT.mkdir(parents=True, exist_ok=True)
    frames[-1].save(OUT / "xjournal.ico", format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    detailed.resize((512, 512), Image.LANCZOS).save(OUT / "xjournal.png")
    print("wrote", OUT / "xjournal.ico", "and", OUT / "xjournal.png")


if __name__ == "__main__":
    main()
