#!/usr/bin/env python3
"""Draws the placeholder Steam Workshop preview (image.png).

A stand-in until a real screenshot of the overlay replaces it: the meter's own
look - a dark window, a title strip, and class-tinted bars - rather than
anything from the game's art, which is MegaCrit's to distribute. Pure standard
library, since this machine has no imaging package.
"""

import struct
import zlib

SIZE = 640
BG = (0x0D, 0x0D, 0x10)
PANEL = (0x14, 0x14, 0x1A)
BORDER = (0x4A, 0x4A, 0x55)
HEADER = (0x22, 0x22, 0x2B)
TRACK = (0x1E, 0x1E, 0x26)
TEXT = (0xEC, 0xEC, 0xF2)
BARS = [
    ((0xC0, 0x39, 0x2B), 0.94),
    ((0x27, 0xAE, 0x60), 0.72),
    ((0x29, 0x80, 0xB9), 0.55),
    ((0xD4, 0xA0, 0x17), 0.34),
]

# A 5x7 pixel font, just the letters the title needs.
GLYPHS = {
    "r": ["     ", "     ", "# ## ", "##  #", "##   ", "##   ", "##   "],
    "D": ["#### ", "##  #", "##  #", "##  #", "##  #", "##  #", "#### "],
    "P": ["#### ", "##  #", "##  #", "#### ", "##   ", "##   ", "##   "],
    "S": [" ####", "##   ", "##   ", " ### ", "   ##", "   ##", "#### "],
    "M": ["#   #", "## ##", "# # #", "# # #", "#   #", "#   #", "#   #"],
    "e": ["     ", "     ", " ### ", "##  #", "#####", "##   ", " ### "],
    "t": [" #   ", " #   ", "#### ", " #   ", " #   ", " #  #", "  ## "],
    " ": ["     "] * 7,
}


def blank(size, color):
    return [[color] * size for _ in range(size)]


def rect(px, x0, y0, x1, y1, color):
    for y in range(max(0, y0), min(len(px), y1)):
        row = px[y]
        for x in range(max(0, x0), min(len(row), x1)):
            row[x] = color


def frame(px, x0, y0, x1, y1, color, width=2):
    rect(px, x0, y0, x1, y0 + width, color)
    rect(px, x0, y1 - width, x1, y1, color)
    rect(px, x0, y0, x0 + width, y1, color)
    rect(px, x1 - width, y0, x1, y1, color)


def text(px, message, x, y, scale, color):
    cursor = x
    for char in message:
        glyph = GLYPHS[char]
        for row, bits in enumerate(glyph):
            for col, bit in enumerate(bits):
                if bit == "#":
                    left = cursor + col * scale
                    top = y + row * scale
                    rect(px, left, top, left + scale, top + scale, color)
        cursor += 6 * scale
    return cursor - x - scale


def write_png(path, px):
    raw = b"".join(
        b"\x00" + b"".join(struct.pack("BBB", *pixel) for pixel in row) for row in px
    )

    def chunk(tag, payload):
        body = tag + payload
        return struct.pack(">I", len(payload)) + body + struct.pack(">I", zlib.crc32(body))

    header = struct.pack(">IIBBBBB", len(px[0]), len(px), 8, 2, 0, 0, 0)
    with open(path, "wb") as out:
        out.write(b"\x89PNG\r\n\x1a\n")
        out.write(chunk(b"IHDR", header))
        out.write(chunk(b"IDAT", zlib.compress(raw, 9)))
        out.write(chunk(b"IEND", b""))


def main():
    px = blank(SIZE, BG)

    panel = (70, 150, SIZE - 70, SIZE - 150)
    rect(px, *panel, PANEL)
    rect(px, panel[0], panel[1], panel[2], panel[1] + 54, HEADER)
    frame(px, *panel, BORDER)

    # Title, centred over the panel header.
    scale = 5
    width = len("rDPS Meter") * 6 * scale - scale
    text(px, "rDPS Meter", (SIZE - width) // 2, panel[1] + 15, scale, TEXT)

    # Rows of class-tinted bars, longest first, the way the meter ranks players.
    top = panel[1] + 92
    left, right = panel[0] + 28, panel[2] - 28
    for color, fill in BARS:
        rect(px, left, top, right, top + 46, TRACK)
        rect(px, left, top, left + int((right - left) * fill), top + 46, color)
        top += 62

    write_png("image.png", px)
    print(f"wrote image.png ({SIZE}x{SIZE})")


if __name__ == "__main__":
    main()
