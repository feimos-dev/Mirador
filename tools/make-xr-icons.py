"""Draws the dashboard's icons (src/Mirador.Xr/Assets/*.png): white shapes on nothing, tinted by the UI.

Needs Pillow: pip install pillow.
"""
import math
from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 128
SCALE = 4  # drawn larger, then scaled down for smooth edges
OUT = Path(__file__).resolve().parent.parent / "src" / "Mirador.Xr" / "Assets"
WHITE = (255, 255, 255, 255)
CLEAR = (255, 255, 255, 0)


def canvas():
    image = Image.new("RGBA", (SIZE * SCALE, SIZE * SCALE), CLEAR)
    return image, ImageDraw.Draw(image)


def s(value):
    return value * SCALE


def save(image, name):
    OUT.mkdir(parents=True, exist_ok=True)
    image.resize((SIZE, SIZE), Image.LANCZOS).save(OUT / name)


def power():
    image, draw = canvas()
    # A ring open at the top, and a bar through the gap.
    draw.arc([s(24), s(28), s(104), s(108)], start=-60, end=240, fill=WHITE, width=s(11))
    draw.rounded_rectangle([s(58), s(14), s(70), s(66)], radius=s(6), fill=WHITE)
    save(image, "power.png")


def recentre():
    image, draw = canvas()
    # A ring with four ticks and a dot: the origin under the head.
    draw.ellipse([s(30), s(30), s(98), s(98)], outline=WHITE, width=s(9))
    for angle in range(0, 360, 90):
        dx, dy = math.cos(math.radians(angle)), math.sin(math.radians(angle))
        draw.line([s(64 + dx * 38), s(64 + dy * 38), s(64 + dx * 56), s(64 + dy * 56)], fill=WHITE, width=s(9))
    draw.ellipse([s(55), s(55), s(73), s(73)], fill=WHITE)
    save(image, "recentre.png")


def desktop():
    image, draw = canvas()
    # A monitor on its stand.
    draw.rounded_rectangle([s(14), s(22), s(114), s(86)], radius=s(8), outline=WHITE, width=s(9))
    draw.rectangle([s(58), s(86), s(70), s(102)], fill=WHITE)
    draw.rounded_rectangle([s(40), s(100), s(88), s(110)], radius=s(5), fill=WHITE)
    save(image, "desktop.png")


def confirm():
    image, draw = canvas()
    draw.line([s(24), s(68), s(52), s(96), s(104), s(34)], fill=WHITE, width=s(13), joint="curve")
    save(image, "confirm.png")


if __name__ == "__main__":
    power()
    recentre()
    desktop()
    confirm()
